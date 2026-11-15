from nasa_space_apps_data_downloader import validate_data_fits_schema, load_all_data
from time import time
import yappi

if __name__ == "__main__":
    yappi.set_clock_type("wall")
    with yappi.run():
        load_all_data.main()
    print("Loaded")
    x = yappi.get_func_stats()
    x.save("yappi-out-3")
    x.print_all()
