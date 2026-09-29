# Deploying to AWS

Two commands once the infrastructure exists:

```bash
source deploy/aws/env.sh
./deploy/aws/deploy.sh
```

That builds both images for Graviton, pushes them to ECR, runs the migration as
its own task, waits for it, and only then rolls the services forward. If the
migration fails, nothing is deployed.

---

## 1. The shape of it

```
            ALB (HTTPS, WebSockets on)
                      │
          ┌───────────┴───────────┐
          ▼                       ▼
   ECS service: api        ECS service: worker
   N tasks, scheduler OFF  exactly 1 task, scheduler ON
          │                       │
          └───────────┬───────────┘
                      │
     ┌────────┬───────┼────────┬─────────┐
     ▼        ▼       ▼        ▼         ▼
    RDS   ElastiCache Amazon   S3    Secrets
 PostgreSQL  (Redis)    MQ   bucket   Manager
```

### Why the worker is a separate service

Quartz here is configured with `UseInMemoryStore()`, so **every process with
the scheduler enabled runs every job**. Two API tasks both scheduling would
dispatch every campaign twice, send every expiry reminder twice, and drain the
email outbox twice — real messages to real customers, billed per conversation.

So the API tasks run with `Scheduler__Enabled=false` and scale freely, and one
worker task runs with it `true`. The worker service must be configured with:

```
--deployment-configuration maximumPercent=100,minimumHealthyPercent=0
--desired-count 1
```

so ECS stops the old task before starting the new one. The default
(`200`/`100`) deliberately overlaps them, which here means two schedulers for
about a minute on every deploy.

The RabbitMQ consumers are a different matter and need no such care: MassTransit
consumers compete for one queue, so running them on every task shares the work
rather than duplicating it. Only the cron schedule has to be singular.

The alternative is Quartz clustering with an `AdoJobStore`, which is the right
answer if you ever need the schedule to survive the worker dying. It needs its
own tables and a migration; the split above does not.

## 2. One-time infrastructure

Nothing here is created by `deploy.sh` — it deploys, it does not provision.

### ECR

```bash
aws ecr create-repository --repository-name marketing-api \
  --image-scanning-configuration scanOnPush=true
```

### S3

```bash
aws s3api create-bucket --bucket marketing-files --region eu-west-1 \
  --create-bucket-configuration LocationConstraint=eu-west-1
aws s3api put-public-access-block --bucket marketing-files \
  --public-access-block-configuration "BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true"
aws s3api put-bucket-encryption --bucket marketing-files \
  --server-side-encryption-configuration '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"}}]}'
```

**A lifecycle rule is worth adding.** Exports expire after seven days in the
application and the sweep deletes the object, but a failed sweep leaves files
paid for indefinitely:

```bash
aws s3api put-bucket-lifecycle-configuration --bucket marketing-files \
  --lifecycle-configuration '{"Rules":[{"ID":"expire-exports","Status":"Enabled","Filter":{"Prefix":"production/exports/"},"Expiration":{"Days":30}}]}'
```

Thirty days rather than seven, deliberately: the application owns the seven-day
promise, and this is only the backstop for when it fails to keep it.

### Secrets Manager

One secret, one JSON object. The task definitions pull individual keys out of
it, so rotating one value is a single update and no value ever appears in a
task definition or in `describe-task-definition` output.

```bash
aws secretsmanager create-secret --name marketing/app --secret-string '{
  "DatabaseConnectionString": "Host=...;Database=marketing;Username=...;Password=...;SSL Mode=Require",
  "RedisConnectionString": "marketing.abc.ng.0001.euw1.cache.amazonaws.com:6379",
  "RabbitHost": "b-xxxx.mq.eu-west-1.amazonaws.com",
  "RabbitUsername": "...",
  "RabbitPassword": "...",
  "JwtSigningKey": "...",
  "WhatsAppAppSecret": "...",
  "WhatsAppWebhookVerifyToken": "...",
  "PlacesApiKey": "...",
  "SmtpPassword": "..."
}'
```

Generate the JWT signing key rather than inventing one: `openssl rand -base64 48`.

### IAM

**Execution role** — what ECS does on the task's behalf. Attach
`AmazonECSTaskExecutionRolePolicy` plus:

```json
{
  "Effect": "Allow",
  "Action": "secretsmanager:GetSecretValue",
  "Resource": "arn:aws:secretsmanager:eu-west-1:ACCOUNT:secret:marketing/app-*"
}
```

**Task role** — what the application itself may do. S3 on its own prefix, and
nothing else:

```json
{
  "Statement": [
    {
      "Effect": "Allow",
      "Action": ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"],
      "Resource": "arn:aws:s3:::marketing-files/production/*"
    },
    {
      "Effect": "Allow",
      "Action": ["s3:ListBucket", "s3:GetBucketLocation"],
      "Resource": "arn:aws:s3:::marketing-files"
    }
  ]
}
```

`GetBucketLocation` is what the `s3` health check calls. Without it the check
reports degraded while everything else works.

**No access keys anywhere.** The SDK's default credential chain finds the task
role, which is why no `AWS_ACCESS_KEY_ID` appears in any task definition.

### Load balancer

- Target group health check path: **`/health/ready`** (not `/health/live` — the
  point of the readiness probe is to keep traffic off an instance that cannot
  reach the database, and not `/health`, which requires authentication).
- **Idle timeout: at least 300 seconds.** SignalR's WebSocket connection is
  long-lived, and the default 60 seconds tears it down repeatedly — which looks
  exactly like the reconnect storm that was diagnosed earlier in this project.
- Stickiness is **not** required, because the Redis backplane is enabled. If you
  turn the backplane off, it becomes required immediately: without either, a
  client connected to task A never receives an event raised on task B, and
  export-finished toasts simply never arrive.

### ElastiCache

Redis is optional to the application — it degrades rather than failing — with
**one exception**: with more than one API task, the SignalR backplane is not
optional. `Redis__UseSignalRBackplane` is set to `true` in the task definitions
for that reason.

## 3. Configuration mapping

.NET reads `Database__ConnectionString` from the environment as
`{"Database": {"ConnectionString": ...}}`. Double underscore is the separator.
That is the whole trick: **every setting in `appsettings.json` is overridable by
an environment variable**, so the ECS `secrets` and `environment` blocks
configure the application completely and no file needs to change per
environment.

## 4. The deploy loop

`deploy.sh` does this, in this order:

1. Log in to ECR.
2. `docker buildx build --platform linux/arm64 --target runtime` → push.
3. Same for `--target migrations`.
4. Render and register all three task definitions.
5. `run-task` the migration, **wait for it, read its exit code**, and abort the
   whole deployment if it is not zero.
6. Update the worker service, then the API service.
7. Wait for both to stabilise.

Rolling back is re-pointing a service at the previous task definition revision;
the last line of the script prints the command. Rolling a *migration* back is
not automatic and never will be — that is what makes step 5 stop the deployment
rather than carry on.

### Images

| Tag | Stage | Roughly |
| --- | --- | --- |
| `:<sha>` | `runtime` | the API |
| `:migrate-<sha>` | `migrations` | a compiled EF bundle |

The migration image used to be the SDK plus the whole source tree — about
1.1 GB, pulled by a task that runs for four seconds. It is now a ~75 MB bundle
on the same base layer the API image already pulled, so in practice the
migration step downloads nothing.

Images are tagged with the commit, never `latest`. `latest` makes a rollback a
guess and makes two environments both running "latest" unknowable.

### ARM64

The task definitions specify Graviton, and the Dockerfile cross-compiles for it
rather than emulating — `--platform=$BUILDPLATFORM` on the SDK stage with
`-a $TARGETARCH` on the build, which is the difference between a two-minute
build and a twenty-minute one under QEMU.

To go back to x86, change `IMAGE_PLATFORM` to `linux/amd64` **and**
`runtimePlatform.cpuArchitecture` to `X86_64` in all three task definitions.
Changing one and not the other gives an `exec format error` at task start.

## 5. Running it locally the way it runs on AWS

The local default still writes files to a volume. To exercise the S3 path:

```bash
# in .env
STORAGE_PROVIDER=S3
S3_BUCKET=marketing-files
S3_SERVICE_URL=http://minio:9000
S3_FORCE_PATH_STYLE=true
AWS_ACCESS_KEY_ID=minioadmin
AWS_SECRET_ACCESS_KEY=<MINIO_ROOT_PASSWORD>

docker compose --profile s3 up --build
```

MinIO stands in for S3 and `minio-init` creates the bucket. This exists because
"it worked on local disk" has never once predicted that a bucket, a prefix and
a task role work together.

Migrations locally:

```bash
docker compose --profile migrate run --rm migrate
```

## 6. Things that will bite, in order of likelihood

1. **Deploying with `Storage__Provider=Local`.** Every export and every uploaded
   import is written to the task's own disk, so a second task cannot read it and
   a redeploy destroys it. Nothing errors — downloads just 404 intermittently,
   which reads as a bug in exports. The task definitions set `S3`; the danger is
   someone copying a local `.env`.
2. **Two schedulers.** Covered above. Symptom: every customer gets each campaign
   twice.
3. **ALB idle timeout left at 60 s.** Symptom: SignalR reconnects endlessly.
4. **`readonlyRootFilesystem` without the `/tmp` mount.** The export writer
   streams each file to disk before uploading. The task definitions include an
   ephemeral volume at `/tmp` for exactly this; removing it breaks exports only,
   and only for large ones.
5. **Architecture mismatch.** `exec format error` in the ECS task logs.
6. **Security groups.** The tasks need outbound 443 (ECR, Secrets Manager, S3,
   Meta's Graph API) and inbound only from the ALB.

## 7. What has not been tested

**No image in this directory has been built.** Docker's daemon is not running on
the development machine, so the Dockerfile changes — the cross-compilation
arguments and the migration-bundle stage — are unverified by a real build.

What *was* verified:

- `dotnet ef migrations bundle` produces a working bundle from this solution
  (75 MB, built successfully on the host).
- `docker compose config` parses, with every service and profile resolving.
- `render.py` produces valid task definitions from all three templates, with
  comments stripped and every placeholder substituted or reported.
- The S3 storage provider's behaviour, by unit test.

**Build both images once before relying on this**, ideally by running
`deploy.sh` against a staging cluster:

```bash
docker buildx build --platform linux/arm64 --target runtime -t test .
docker buildx build --platform linux/arm64 --target migrations -t test-migrate .
```
