import json
from pathlib import Path
from typing import Any, Iterable

from python_linq import Enumerable


def stream_parse(filename: Path) -> Iterable[dict[str, Any]]:
    buffer: list[str] = []
    with open(filename, "r", encoding="UTF8") as file:
        for line in Enumerable(file).skip(1):
            if "{" in line:
                pass
            elif "}" in line:
                x = f"{{{"".join(buffer)}}}"
                try:
                    yield json.loads(x)
                except Exception as e:
                    print(x)
                    print(e)
                    break
                buffer = []
            else:
                buffer.append(line)
