"""Regression tests for PostgreSQL DDL (SQLite will accept integer BOOLEAN defaults)."""

import re

from sqlalchemy import Boolean
from sqlalchemy.dialects import postgresql
from sqlalchemy.schema import CreateTable

from server.app.main import Base, HouseholdCatalogueItem


def test_postgres_catalogue_enabled_default_is_boolean():
    column = HouseholdCatalogueItem.__table__.c.enabled
    assert isinstance(column.type, Boolean)
    assert column.server_default is not None
    assert str(column.server_default.arg).strip().lower() == "true"
    ddl = str(CreateTable(HouseholdCatalogueItem.__table__).compile(dialect=postgresql.dialect()))
    assert re.search(r"enabled\s+BOOLEAN\s+DEFAULT\s+true\s+NOT NULL", ddl, re.I), ddl


def test_no_postgres_boolean_column_has_integer_default():
    # CreateTable compiles SQL for all tables without needing a running PostgreSQL server.
    # Explicitly enforce boolean-literal defaults because PostgreSQL rejects DEFAULT 1/0.
    for table in Base.metadata.sorted_tables:
        ddl = str(CreateTable(table).compile(dialect=postgresql.dialect()))
        assert "CREATE TABLE" in ddl
        for column in table.columns:
            if isinstance(column.type, Boolean) and column.server_default is not None:
                literal = str(column.server_default.arg).strip().lower()
                assert literal in ("true", "false"), (table.name, column.name, literal)
