"""Rename PrivacyToggle brand to NEXT-Toggle (NextToggle in C# identifiers)."""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SKIP_DIRS = {".git", "bin", "obj", "publish", "Output", "agent-transcripts"}

# Longest-first identifier / path replacements
REPLACEMENTS = [
    ("PrivacyToggle.Properties", "NextToggle.Properties"),
    ("PrivacyToggle.Helper", "NextToggle.Helper"),
    ("PrivacyToggle.Services", "NextToggle.Services"),
    ("PrivacyToggle.Models", "NextToggle.Models"),
    ("PrivacyToggle.Views", "NextToggle.Views"),
    ("PrivacyToggle.Tray", "NextToggle.Tray"),
    ("PrivacyToggle.Interop", "NextToggle.Interop"),
    ("namespace PrivacyToggle", "namespace NextToggle"),
    ("PrivacyToggle.App", "NextToggle.App"),
    ("PrivacyToggle.Program", "NextToggle.Program"),
    ("PrivacyToggleHelper", "NEXTToggleHelper"),
    ("PrivacyToggle Helper", "NEXT-Toggle Helper"),
    ("PrivacyToggle.CameraHelper", "NEXT-Toggle.CameraHelper"),
    ("PrivacyToggleHotkeyWindow", "NEXTToggleHotkeyWindow"),
    ("PrivacyToggle.TaskbarSpeed.AppBar", "NEXT-Toggle.TaskbarSpeed.AppBar"),
    ("PrivacyToggle-NetworkUsage-Diagnostic", "NEXT-Toggle-NetworkUsage-Diagnostic"),
    ("PrivacyToggle-NetworkUsage", "NEXT-Toggle-NetworkUsage"),
    ("PrivacyToggle-Update", "NEXT-Toggle-Update"),
    ("PrivacyToggle-Setup", "NEXT-Toggle-Setup"),
    ("PrivacyToggle.exe", "NEXT-Toggle.exe"),
    ("PrivacyToggle.csproj", "NEXT-Toggle.csproj"),
    ("PrivacyToggle.sln", "NEXT-Toggle.sln"),
    ("PrivacyToggle.iss", "NEXT-Toggle.iss"),
    ('"PrivacyToggle.Properties.Resources"', '"NextToggle.Properties.Resources"'),
    ("PrivacyToggle|", "NEXT-Toggle|"),
    ('ParseAdd($"PrivacyToggle/', 'ParseAdd($"NEXT-Toggle/'),
    ('name="PrivacyToggle.app"', 'name="NEXT-Toggle.app"'),
    ("StartupObject>PrivacyToggle.Program", "StartupObject>NextToggle.Program"),
    ("RootNamespace>PrivacyToggle", "RootNamespace>NextToggle"),
    ("AssemblyName>PrivacyToggle", "AssemblyName>NEXT-Toggle"),
    ("Product>PrivacyToggle", "Product>NEXT-Toggle"),
    ('= "PrivacyToggle", "src\\PrivacyToggle\\PrivacyToggle.csproj"', '= "NEXT-Toggle", "src\\NextToggle\\NEXT-Toggle.csproj"'),
    ("src\\PrivacyToggle", "src\\NextToggle"),
    ("src/PrivacyToggle", "src/NextToggle"),
    ("PrivacyToggle.SingleInstance", "NEXT-Toggle.SingleInstance"),
    ("PrivacyToggle.ShowSettings", "NEXT-Toggle.ShowSettings"),
    ("PrivacyToggle.Exit", "NEXT-Toggle.Exit"),
    ('", "PrivacyToggle")', '", "NEXT-Toggle")'),
    ("Notify_Title_PrivacyToggle", "Notify_Title_Brand"),
    ("#define AppName \"PrivacyToggle\"", '#define AppName "NEXT-Toggle"'),
    ("#define AppExe \"PrivacyToggle.exe\"", '#define AppExe "NEXT-Toggle.exe"'),
    ("OutputBaseFilename=PrivacyToggle-Setup", "OutputBaseFilename=NEXT-Toggle-Setup"),
    ("#define ServiceName \"PrivacyToggleHelper\"", '#define ServiceName "NEXTToggleHelper"'),
]

FIX_AFTER_BRAND = [
    (re.compile(r"namespace NEXT-Toggle"), "namespace NextToggle"),
    (re.compile(r"using NEXT-Toggle\."), "using NextToggle."),
    (re.compile(r"clr-namespace:NEXT-Toggle"), "clr-namespace:NextToggle"),
    (re.compile(r"x:Class=\"NEXT-Toggle\."), 'x:Class="NextToggle.'),
    (re.compile(r"NEXT-Toggle\.Program"), "NextToggle.Program"),
    (re.compile(r'"NEXT-Toggle\.Properties\.Resources"'), '"NextToggle.Properties.Resources"'),
    (re.compile(r"PrivacyToggle\.Properties"), "NextToggle.Properties"),
    (re.compile(r"Project\(\"{FAE04EC0[^\"]+\"\) = \"PrivacyToggle\""), 'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "NEXT-Toggle"'),
]

TEXT_SUFFIXES = {
    ".cs", ".xaml", ".csproj", ".sln", ".iss", ".ps1", ".md", ".tsv", ".py", ".resx", ".manifest", ".json",
}


def should_process(path: Path) -> bool:
    if any(part in SKIP_DIRS for part in path.parts):
        return False
    return path.suffix.lower() in TEXT_SUFFIXES or path.name in {"build.ps1", "make-icons.ps1"}


def transform(text: str) -> str:
    for old, new in REPLACEMENTS:
        text = text.replace(old, new)
    text = text.replace("PrivacyToggle", "NEXT-Toggle")
    for pat, repl in FIX_AFTER_BRAND:
        text = pat.sub(repl, text)
    return text


def main() -> None:
    count = 0
    for path in ROOT.rglob("*"):
        if not path.is_file() or not should_process(path):
            continue
        if path.name == "rename_brand.py":
            continue
        raw = path.read_text(encoding="utf-8")
        new = transform(raw)
        if new != raw:
            path.write_text(new, encoding="utf-8")
            count += 1
    print(f"Updated {count} files under {ROOT}")


if __name__ == "__main__":
    main()
