import json
from pathlib import Path
from typing import Any, Callable, Iterable

from python_linq import Enumerable


def stream_parse(filename: Path, *, chunks: int = 1) -> Iterable[dict[str, Any]]:
    get_buffer: Callable[[], list[list[str]]] = lambda: [[] for _ in range(chunks)]
    buffer = get_buffer()
    current_chunk = 1
    with open(filename, "r", encoding="UTF8") as file:
        for line in Enumerable(file).skip(1):
            if "{" in line:
                pass
            elif "]" == line.strip():
                pass
            elif "}" in line:
                if current_chunk == chunks:
                    x = f"[{",".join(f"{{{"".join(chunk)}}}" for chunk in buffer)}]"
                    try:
                        yield from json.loads(x)
                    except Exception as e:
                        print(x)
                        print(e)
                        raise
                    buffer = get_buffer()
                    current_chunk = 1
                else:
                    current_chunk += 1
            else:
                buffer[current_chunk - 1].append(line)
        for chunk in buffer:
            if len(chunk) > 0:
                x = f"{{{"".join(chunk)}}}"
                try:
                    yield json.loads(x)
                except:
                    print(x)
                    raise
