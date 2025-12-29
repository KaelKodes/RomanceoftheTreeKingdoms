from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker
from src.database.models import Base

DATABASE_URL = "sqlite:///tree_kingdoms.db"

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
