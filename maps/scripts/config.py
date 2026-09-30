"""Loads maps.yaml (needs PyYAML: `pip install pyyaml`)."""
import sys
from pathlib import Path


def load_config(path):
    path = Path(path)
    if not path.exists():
        example = path.with_name("maps.example.yaml")
        sys.exit(f"Missing {path.name}. Copy {example.name} to {path.name} and set your vault path.")
    try:
        import yaml
    except ImportError:
        sys.exit("PyYAML is required: pip install pyyaml")
    with path.open(encoding="utf-8") as f:
        return yaml.safe_load(f) or {}
