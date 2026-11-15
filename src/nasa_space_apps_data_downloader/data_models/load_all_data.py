from multiprocessing import Pool
from pathlib import Path
import sys
from time import time
from typing import Sequence

from python_linq import Enumerable

from ..config import PROJECT_ROOT, Config
from .base_sensor_data import BaseSensorData
from .streaming_json_parser import (
    stream_parse_buffer_by_characters,
)
import pickle


def main():
    t = time()
    with Pool() as p:

        x = (
            Enumerable(sys.argv[1:])
            .map(Path)
            .map(lambda x: Enumerable(stream_parse_buffer_by_characters(x)))
            .flat_map(
                lambda x: p.imap_unordered(
                    BaseSensorData.try_from_object, x, chunksize=100_000 // 16
                )
            )
            .where(lambda x: x is not None)
            .to_list()
        )
    print(f"Loaded all data, size = {len(x)} in {time() - t} seconds!")
    t = time()
    with open(PROJECT_ROOT / "data.pickle", "wb") as file:
        pickle.dump(x, file)
    print(f"Pickled data in {time() - t} seconds!")
    del x
    t = time()
    with open(PROJECT_ROOT / "data.pickle", "rb") as file:
        y = pickle.load(file)
    print(f"Unpickled data in {time() - t} seconds!")


class DataAccessor:

    _pickled_data = None

    @classmethod
    def pickled_data(cls):
        with open(Config.pickle_data_path, "rb") as file:
            cls._pickled_data = pickle.load(file)
        return cls._pickled_data

    _json_data = None

    @classmethod
    def json_data(cls) -> Sequence[BaseSensorData]:
        with Pool() as p:
            cls._json_data = (
                Enumerable(sys.argv[1:])
                .map(Path)
                .map(lambda x: Enumerable(stream_parse_buffer_by_characters(x)))
                .flat_map(
                    lambda x: p.imap_unordered(
                        BaseSensorData.try_from_object, x, chunksize=100_000 // 16
                    )
                )
                .where(lambda x: x is not None)
                .cast(BaseSensorData)
                .to_list()
            )
        return cls._json_data
