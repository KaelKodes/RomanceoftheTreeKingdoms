import os
from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker
from src.database.models import Base

# Get the absolute path to the directory containing this script (src/database/)
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
# The database file is in the project root
DB_PATH = os.path.abspath(os.path.join(BASE_DIR, "../../tree_kingdoms.db"))
DATABASE_URL = f"sqlite:///{DB_PATH}"

class DatabaseManager:
    def __init__(self, db_url=DATABASE_URL):
        self.engine = create_engine(db_url)
        self.SessionLocal = sessionmaker(autocommit=False, autoflush=False, bind=self.engine)

    def init_db(self):
        """Creates tables if they don't exist."""
        Base.metadata.create_all(self.engine)

    def get_session(self):
        """Returns a new session."""
        return self.SessionLocal()

# Global instance
db = DatabaseManager()
