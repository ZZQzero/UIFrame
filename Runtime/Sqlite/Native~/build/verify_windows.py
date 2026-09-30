"""Inspect a PE DLL without loading it. Uses only Python's standard library."""
import argparse
import json
from pathlib import Path
import re
import struct


def inspect(path, header):
    data = Path(path).read_bytes()

    def unpack(fmt, offset):
        return struct.unpack_from('<' + fmt, data, offset)

    pe, = unpack('I', 0x3c)
    if data[:2] != b'MZ' or data[pe:pe + 4] != b'PE\0\0':
        raise ValueError('Not a PE binary')
    machine, sections = unpack('HH', pe + 4)
    optional_size, characteristics = unpack('HH', pe + 20)
    optional = pe + 24
    if machine != 0x8664 or unpack('H', optional)[0] != 0x20b or not characteristics & 0x2000:
        raise ValueError('Expected a Windows x86_64 PE32+ DLL')
    section_table = optional + optional_size

    def offset(rva):
        for i in range(sections):
            virtual_size, start, raw_size, raw = unpack('IIII', section_table + i * 40 + 8)
            if start <= rva < start + max(virtual_size, raw_size):
                if rva - start >= raw_size:
                    raise ValueError('RVA has no file contents')
                return raw + rva - start
        raise ValueError('RVA is outside PE sections')

    def string(rva):
        start = offset(rva)
        return data[start:data.index(b'\0', start)].decode('ascii')

    def directory(index):
        return unpack('II', optional + 112 + index * 8)

    export_rva, export_size = directory(0)
    export = offset(export_rva)
    function_count, count, functions, names, ordinals = unpack('IIIII', export + 20)
    exported = [string(unpack('I', offset(names) + i * 4)[0]) for i in range(count)]
    expected = set(re.findall(r'UFSQLITE_API[^;]*?\b(ufsqlite_\w+)\(', Path(header).read_text(encoding="utf-8")))
    if not expected or set(exported) != expected or function_count != count:
        raise ValueError('DLL exports do not match the declared C ABI: ' + repr(exported))
    for i in range(count):
        ordinal, = unpack('H', offset(ordinals) + 2 * i)
        address, = unpack('I', offset(functions) + 4 * ordinal)
        if export_rva <= address < export_rva + export_size:
            raise ValueError('The C ABI must be implemented in this DLL, not forwarded')
    imports = []
    import_rva, _ = directory(1)
    if import_rva:
        entry = offset(import_rva)
        while any(unpack('IIIII', entry)):
            imports.append(string(unpack('I', entry + 12)[0]))
            entry += 20
    if directory(13)[0]:
        raise ValueError('Delay-loaded dependencies are not part of the plugin contract')
    system_dlls = {'kernel32.dll', 'advapi32.dll', 'user32.dll', 'ntdll.dll', 'ucrtbase.dll', 'bcrypt.dll'}
    for name in imports:
        normalized = name.lower()
        if normalized not in system_dlls and not normalized.startswith('api-ms-win-crt-'):
            raise ValueError('Unexpected external runtime dependency: ' + name)
    return dict(machine='x86_64', exports=sorted(exported), imports=sorted(imports))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('dll', type=Path)
    args = parser.parse_args()
    print(json.dumps(inspect(args.dll, Path(__file__).resolve().parents[1]/'include/ufsqlite.h'), indent=2))
