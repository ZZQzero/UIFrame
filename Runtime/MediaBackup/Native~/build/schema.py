"""Turn the single schema source into bounded core commands (including triggers)."""
import pathlib
import sqlite3
import sys

source, target = map(pathlib.Path, sys.argv[1:])
commands, pending = [], ''
for line in source.read_text().splitlines(True):
    pending += line
    if sqlite3.complete_statement(pending):
        commands.append(pending.strip())
        pending = ''
if pending.strip():
    raise ValueError('Incomplete schema statement')
target.write_text('static const char *const catalog_schema[] = {\n' +
                  ',\n'.join('R"ufschema(' + sql + ')ufschema"' for sql in commands) + '\n};\n')
