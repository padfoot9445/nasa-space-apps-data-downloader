from nasa_space_apps_data_downloader import validate_data_fits_schema
from time import time
import yappi

from nasa_space_apps_data_downloader.data_models import DataAccessor

if __name__ == "__main__":
    DataAccessor.pickled_data()
