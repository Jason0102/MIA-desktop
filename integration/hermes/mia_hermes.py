"""Run the pinned Hermes CLI with installed dependencies and MIA's own profile."""

import os
import sys

sys.path.insert(0, os.environ["MIA_HERMES_SOURCE"])
os.environ["HERMES_DISABLE_LAZY_INSTALLS"] = "1"

from hermes_cli.main import main

if __name__ == "__main__":
    main()
