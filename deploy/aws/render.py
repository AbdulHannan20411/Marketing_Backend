#!/usr/bin/env python3
"""Turns a commented task-definition template into something the AWS CLI accepts.

Two jobs, and the second is not optional:

1. Substitute ``${NAME}`` placeholders from the environment. Any placeholder
   with no value is an error rather than an empty string - a task definition
   with a blank image or a blank role ARN fails at ``run-task`` with a message
   that does not mention which field was empty.

2. Strip every key beginning with ``//``. The templates are commented so that
   the reasoning lives next to the setting it explains, but
   ``register-task-definition`` validates its input strictly and rejects
   unknown members. Without this step every comment is a deployment failure.

Usage:
    python3 render.py task-def-api.json > rendered.json
"""

import json
import os
import re
import sys

PLACEHOLDER = re.compile(r"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")


def substitute(text: str) -> str:
    """Replaces ${NAME} with the environment's value, refusing to guess."""
    missing = []

    def replace(match: "re.Match[str]") -> str:
        name = match.group(1)
        value = os.environ.get(name)

        # An empty string is treated as missing on purpose. Several of these
        # fields are optional-looking but not optional in practice, and an
        # empty S3 prefix is very different from an unset one.
        if value is None or value == "":
            missing.append(name)
            return ""

        return value

    rendered = PLACEHOLDER.sub(replace, text)

    if missing:
        unique = sorted(set(missing))
        sys.exit(f"render.py: not set in the environment: {', '.join(unique)}")

    return rendered


def strip_comments(node):
    """Removes //-prefixed keys, at every depth."""
    if isinstance(node, dict):
        return {
            key: strip_comments(value)
            for key, value in node.items()
            if not key.startswith("//")
        }

    if isinstance(node, list):
        return [strip_comments(item) for item in node]

    return node


def main() -> None:
    if len(sys.argv) != 2:
        sys.exit("usage: render.py <template.json>")

    with open(sys.argv[1], encoding="utf-8") as handle:
        template = json.load(handle)

    # Comments are stripped *before* substitution, not after. A comment is free
    # to mention a ${PLACEHOLDER} while explaining what the placeholders are,
    # and the other order made that an error - which it was, the first time
    # this ran, because the API template opens by doing exactly that.
    document = strip_comments(template)

    rendered = substitute(json.dumps(document, indent=2))

    # Re-parsed so a substituted value that breaks the JSON - one containing an
    # unescaped quote, say - is caught here rather than by the AWS CLI three
    # steps later with a much worse message.
    json.dump(json.loads(rendered), sys.stdout, indent=2)
    sys.stdout.write("\n")


if __name__ == "__main__":
    main()
