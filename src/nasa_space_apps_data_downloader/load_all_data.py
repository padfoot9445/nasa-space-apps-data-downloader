from multiprocessing import Pool, dummy
from pathlib import Path
import sys

from python_linq import Enumerable

from nasa_space_apps_data_downloader.data_models.base_sensor_data import BaseSensorData
from nasa_space_apps_data_downloader.data_models.streaming_json_parser import (
    stream_parse_buffer_by_characters,
)


def main():
    with Pool() as p:

        x = (
            Enumerable(sys.argv[1:])
            .map(Path)
            .map(lambda x: Enumerable(stream_parse_buffer_by_characters(x)))
            .flat_map(
                lambda x: p.imap_unordered(
                    BaseSensorData.from_object, x, chunksize=100_000 // 16
                )
            )
            .to_list()
        )
    print(f"Loaded all data, size = {len(x)}!")
