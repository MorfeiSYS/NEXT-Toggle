"""Generate Properties/Resources.resx and Resources.ru.resx from tools/i18n.tsv (key\\ten\\tru)."""
from __future__ import annotations

import xml.sax.saxutils as xu
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent / "src" / "PrivacyToggle" / "Properties"
TSV = Path(__file__).resolve().parent / "i18n.tsv"


def resx_header(culture: str | None) -> str:
    lines = [
        '<?xml version="1.0" encoding="utf-8"?>',
        '<root>',
        '  <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">',
        '    <xsd:import namespace="http://www.w3.org/XML/1998/namespace" />',
        '    <xsd:element name="root" msdata:IsDataSet="true">',
        '      <xsd:complexType>',
        '        <xsd:choice maxOccurs="unbounded">',
        '          <xsd:element name="metadata">',
        '            <xsd:complexType>',
        '              <xsd:sequence>',
        '                <xsd:element name="value" type="xsd:string" minOccurs="0" />',
        '              </xsd:sequence>',
        '              <xsd:attribute name="name" use="required" type="xsd:string" />',
        '              <xsd:attribute name="type" type="xsd:string" />',
        '              <xsd:attribute name="mimetype" type="xsd:string" />',
        '              <xsd:attribute ref="xml:space" />',
        '            </xsd:complexType>',
        '          </xsd:element>',
        '          <xsd:element name="assembly">',
        '            <xsd:complexType>',
        '              <xsd:attribute name="alias" type="xsd:string" />',
        '              <xsd:attribute name="name" type="xsd:string" />',
        '            </xsd:complexType>',
        '          </xsd:element>',
        '          <xsd:element name="data">',
        '            <xsd:complexType>',
        '              <xsd:sequence>',
        '                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />',
        '                <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />',
        '              </xsd:sequence>',
        '              <xsd:attribute name="name" type="xsd:string" use="required" msdata:Ordinal="1" />',
        '              <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />',
        '              <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />',
        '              <xsd:attribute ref="xml:space" />',
        '            </xsd:complexType>',
        '          </xsd:element>',
        '          <xsd:element name="resheader">',
        '            <xsd:complexType>',
        '              <xsd:sequence>',
        '                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />',
        '              </xsd:sequence>',
        '              <xsd:attribute name="name" type="xsd:string" use="required" />',
        '            </xsd:complexType>',
        '          </xsd:element>',
        '        </xsd:choice>',
        '      </xsd:complexType>',
        '    </xsd:element>',
        '  </xsd:schema>',
        '  <resheader name="resmimetype">',
        '    <value>text/microsoft-resx</value>',
        '  </resheader>',
        '  <resheader name="version">',
        '    <value>2.0</value>',
        '  </resheader>',
        '  <resheader name="reader">',
        '    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>',
        '  </resheader>',
        '  <resheader name="writer">',
        '    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>',
        '  </resheader>',
    ]
    if culture:
        lines.append(f'  <data name="Culture"><value>{culture}</value></data>')
    return "\n".join(lines)


def write_resx(path: Path, culture: str | None, values: dict[str, str]) -> None:
    parts = [resx_header(culture)]
    for key in sorted(values.keys()):
        val = values[key].replace("\r\n", "\n").replace("\n", "&#10;")
        parts.append(f'  <data name="{xu.escape(key, {"\"": "&quot;"})}" xml:space="preserve">')
        parts.append(f"    <value>{xu.escape(val)}</value>")
        parts.append("  </data>")
    parts.append("</root>")
    path.write_text("\n".join(parts) + "\n", encoding="utf-8")


def load_tsv() -> list[tuple[str, str, str]]:
    rows: list[tuple[str, str, str]] = []
    for line in TSV.read_text(encoding="utf-8").splitlines():
        line = line.rstrip("\n")
        if not line or line.startswith("#"):
            continue
        parts = line.split("\t")
        if len(parts) < 3:
            raise ValueError(f"Bad TSV line: {line!r}")
        key, en, ru = parts[0], parts[1], parts[2]
        en = en.replace("\\n", "\n")
        ru = ru.replace("\\n", "\n")
        rows.append((key, en, ru))
    return rows


def main() -> None:
    import subprocess
    import sys

    rows = load_tsv()
    en = {k: e for k, e, _ in rows}
    ru = {k: r for k, _, r in rows}
    if len(en) != len(rows):
        raise SystemExit("Duplicate keys in i18n.tsv")
    ROOT.mkdir(parents=True, exist_ok=True)
    write_resx(ROOT / "Resources.resx", None, en)
    write_resx(ROOT / "Resources.ru.resx", "ru", ru)
    print(f"Wrote {len(rows)} keys to {ROOT}")
    subprocess.run([sys.executable, str(Path(__file__).resolve().parent / "generate_designer.py")], check=True)


if __name__ == "__main__":
    main()
