from pathlib import Path

from .streaming_json_parser import (
    stream_parse,  # pyright: ignore[reportDeprecated]
    stream_parse_buffer_by_characters,
)


def test_stream_parse():
    src = Path(
        "/home/padfoot9445/Coding/nasa-space-apps-data-downloader/data/DL_FIRE_J1V-C2_847738/fire_archive_J1V-C2_847738.json"
    )
    y = stream_parse(src, chunks=100)  # pyright: ignore[reportDeprecated]
    i = 0
    for _ in y:
        i += 1

    assert i == 715433


def test_stream_parse_bytes_returns_correct_size():
    src = Path(
        "/home/padfoot9445/Coding/nasa-space-apps-data-downloader/data/DL_FIRE_J1V-C2_847738/fire_archive_J1V-C2_847738.json"
    )
    y = stream_parse_buffer_by_characters(src)
    i = 0
    for _ in y:
        i += 1

    assert i == 715433


def test_stream_parse_bytes_returns_same_as_stream_parse():
    src = Path(
        "/home/padfoot9445/Coding/nasa-space-apps-data-downloader/data/DL_FIRE_J1V-C2_847738/fire_archive_J1V-C2_847738.json"
    )
    for x, y in zip(
        stream_parse(src, chunks=100),  # pyright: ignore[reportDeprecated]
        stream_parse_buffer_by_characters(src),
        strict=True,
    ):
        assert x == y
