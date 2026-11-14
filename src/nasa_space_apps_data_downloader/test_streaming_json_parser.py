from pathlib import Path

from .streaming_json_parser import stream_parse


def test_stream_parse():
    src = Path(
        "/home/padfoot9445/Coding/nasa-space-apps-data-downloader/data/DL_FIRE_J1V-C2_847738/fire_archive_J1V-C2_847738.json"
    )
    y = stream_parse(src)
    i = 0
    for _ in y:
        i += 1
    print(i)
