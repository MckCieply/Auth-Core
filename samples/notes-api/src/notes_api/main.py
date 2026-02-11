"""The ASGI application of the container: `uvicorn notes_api.main:app`."""

from .app import create_app
from .settings import Settings

app = create_app(Settings.from_env())
