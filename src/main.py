from python_linq import Enumerable

from nasa_space_apps_data_downloader import validate_data_fits_schema
from time import time
import yappi

from nasa_space_apps_data_downloader.data_models import DataAccessor
from nasa_space_apps_data_downloader.data_models.modis import ModisSensorData

if __name__ == "__main__":
    print(
        "none_count: ",
        Enumerable(DataAccessor.pickled_data())
        .of_type(ModisSensorData)
        .where(lambda x: x.type is None)
        .count(),
    )
