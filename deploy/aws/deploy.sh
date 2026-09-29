#!/usr/bin/env bash
#
# Build, push, migrate, deploy. One command, in the only order that is safe.
#
#   source deploy/aws/env.sh
#   ./deploy/aws/deploy.sh
#
# The order is the point. Migrations run as their own task and must succeed
# before either service is updated, because the alternative - migrating on
# start-up - has several tasks racing to change the same schema while the
# previous version is still serving traffic from it.

set -euo pipefail

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly ROOT_DIR="$(cd "${SCRIPT_DIR}/../.." && pwd)"

# ── Preconditions ────────────────────────────────────────────────────────────
for required in PROJECT AWS_REGION AWS_ACCOUNT_ID ECR_REGISTRY ECR_REPOSITORY \
                ECS_CLUSTER ECS_API_SERVICE ECS_WORKER_SERVICE \
                SUBNET_IDS SECURITY_GROUP_IDS \
                EXECUTION_ROLE_ARN TASK_ROLE_ARN SECRET_ARN \
                S3_BUCKET S3_PREFIX CLIENT_BASE_URL JWT_ISSUER JWT_AUDIENCE \
                IMAGE_PLATFORM; do
    if [[ -z "${!required:-}" ]]; then
        echo "deploy.sh: ${required} is not set. Did you source deploy/aws/env.sh?" >&2
        exit 1
    fi
done

for tool in aws docker python3; do
    command -v "${tool}" >/dev/null 2>&1 || { echo "deploy.sh: ${tool} is required." >&2; exit 1; }
done

# The commit is the tag. "latest" is not a deployment identifier: it makes a
# rollback a guess and makes two environments running "latest" unknowable.
GIT_SHA="$(git -C "${ROOT_DIR}" rev-parse --short HEAD)"
readonly GIT_SHA

export IMAGE="${ECR_REGISTRY}/${ECR_REPOSITORY}:${GIT_SHA}"
export MIGRATE_IMAGE="${ECR_REGISTRY}/${ECR_REPOSITORY}:migrate-${GIT_SHA}"

echo "==> Deploying ${GIT_SHA} to ${ECS_CLUSTER} (${AWS_REGION})"

# ── 1. Build and push ────────────────────────────────────────────────────────
echo "==> Logging in to ECR"
aws ecr get-login-password --region "${AWS_REGION}" \
    | docker login --username AWS --password-stdin "${ECR_REGISTRY}"

# buildx, because the build host is usually x86 and the tasks usually run on
# Graviton. The Dockerfile cross-compiles rather than emulating, so this is not
# the twenty-minute QEMU build it would otherwise be.
echo "==> Building ${IMAGE_PLATFORM}"
docker buildx build \
    --platform "${IMAGE_PLATFORM}" \
    --target runtime \
    --tag "${IMAGE}" \
    --cache-from "type=registry,ref=${ECR_REGISTRY}/${ECR_REPOSITORY}:buildcache" \
    --cache-to "type=registry,ref=${ECR_REGISTRY}/${ECR_REPOSITORY}:buildcache,mode=max" \
    --push \
    "${ROOT_DIR}"

echo "==> Building the migration image"
docker buildx build \
    --platform "${IMAGE_PLATFORM}" \
    --target migrations \
    --tag "${MIGRATE_IMAGE}" \
    --push \
    "${ROOT_DIR}"

# ── 2. Register task definitions ─────────────────────────────────────────────
register() {
    local template="$1"
    local rendered
    rendered="$(mktemp)"

    python3 "${SCRIPT_DIR}/render.py" "${SCRIPT_DIR}/${template}" > "${rendered}"

    aws ecs register-task-definition \
        --region "${AWS_REGION}" \
        --cli-input-json "file://${rendered}" \
        --query 'taskDefinition.taskDefinitionArn' \
        --output text

    rm -f "${rendered}"
}

echo "==> Registering task definitions"
MIGRATE_TASK_DEF="$(register task-def-migrate.json)"
API_TASK_DEF="$(register task-def-api.json)"
WORKER_TASK_DEF="$(register task-def-worker.json)"

# ── 3. Migrate, and stop here if it fails ────────────────────────────────────
echo "==> Applying migrations (${MIGRATE_TASK_DEF##*/})"

NETWORK="awsvpcConfiguration={subnets=[${SUBNET_IDS}],securityGroups=[${SECURITY_GROUP_IDS}],assignPublicIp=${ASSIGN_PUBLIC_IP:-DISABLED}}"

TASK_ARN="$(aws ecs run-task \
    --region "${AWS_REGION}" \
    --cluster "${ECS_CLUSTER}" \
    --task-definition "${MIGRATE_TASK_DEF}" \
    --launch-type FARGATE \
    --network-configuration "${NETWORK}" \
    --query 'tasks[0].taskArn' \
    --output text)"

echo "    waiting for ${TASK_ARN##*/}"
aws ecs wait tasks-stopped \
    --region "${AWS_REGION}" \
    --cluster "${ECS_CLUSTER}" \
    --tasks "${TASK_ARN}"

EXIT_CODE="$(aws ecs describe-tasks \
    --region "${AWS_REGION}" \
    --cluster "${ECS_CLUSTER}" \
    --tasks "${TASK_ARN}" \
    --query 'tasks[0].containers[0].exitCode' \
    --output text)"

if [[ "${EXIT_CODE}" != "0" ]]; then
    echo "deploy.sh: migration exited ${EXIT_CODE}. Nothing was deployed." >&2
    echo "           logs: /ecs/${PROJECT}-migrate, stream prefix migrate" >&2
    exit 1
fi

echo "    migrations applied"

# ── 4. Roll the services forward ─────────────────────────────────────────────
# The worker first and alone. Its service is configured with
# minimumHealthyPercent 0 / maximumPercent 100, so ECS stops the old task
# before starting the new one - which is what stops two schedulers existing at
# the same instant and dispatching every due campaign twice.
echo "==> Updating ${ECS_WORKER_SERVICE}"
aws ecs update-service \
    --region "${AWS_REGION}" \
    --cluster "${ECS_CLUSTER}" \
    --service "${ECS_WORKER_SERVICE}" \
    --task-definition "${WORKER_TASK_DEF}" \
    --no-cli-pager \
    --query 'service.serviceName' \
    --output text

echo "==> Updating ${ECS_API_SERVICE}"
aws ecs update-service \
    --region "${AWS_REGION}" \
    --cluster "${ECS_CLUSTER}" \
    --service "${ECS_API_SERVICE}" \
    --task-definition "${API_TASK_DEF}" \
    --no-cli-pager \
    --query 'service.serviceName' \
    --output text

echo "==> Waiting for both services to stabilise"
aws ecs wait services-stable \
    --region "${AWS_REGION}" \
    --cluster "${ECS_CLUSTER}" \
    --services "${ECS_API_SERVICE}" "${ECS_WORKER_SERVICE}"

echo "==> Deployed ${GIT_SHA}"
echo "    rollback: aws ecs update-service --cluster ${ECS_CLUSTER} --service ${ECS_API_SERVICE} --task-definition <previous-revision>"
