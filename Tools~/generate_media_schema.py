"""Generate the managed library schema from its single SQL source."""
from pathlib import Path
import sqlite3
root = Path(__file__).resolve().parents[1]
source = root / 'Runtime/MediaStorage/Schema~/library.sql'
target = root / 'Runtime/MediaStorage/LibrarySchema.Generated.cs'
commands, pending = [], ''
for line in source.read_text().splitlines(True):
    pending += line
    if sqlite3.complete_statement(pending):
        commands.append(pending.strip())
        pending = ''
if pending.strip():
    raise ValueError('Incomplete library schema statement')
text = '// Generated from Schema~/library.sql. Regenerate with Tools~/generate_media_schema.py.\nnamespace Game.Media.Storage\n{\n    internal static class LibrarySchema\n    {\n        internal static readonly string[] Commands = {\n'
text += ',\n'.join('            @"' + sql.replace('"', '""') + '"' for sql in commands)
text += '\n        };\n    }\n}\n'
target.write_text(text)
