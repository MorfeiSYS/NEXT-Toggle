import re
from pathlib import Path

RESX = Path(__file__).resolve().parent.parent / "src" / "NextToggle" / "Properties" / "Resources.resx"
OUT = RESX.parent / "Resources.Designer.cs"

text = RESX.read_text(encoding="utf-8")
keys = [k for k in re.findall(r'<data name="([^"]+)"', text) if k != "Culture"]

lines = [
    "#nullable enable",
    "namespace NextToggle.Properties;",
    "",
    "using System.Globalization;",
    "",
    "public class Resources",
    "{",
    "    private static global::System.Resources.ResourceManager? resourceMan;",
    "    private static CultureInfo? resourceCulture;",
    "",
    "    internal Resources() { }",
    "",
    "    public static global::System.Resources.ResourceManager ResourceManager",
    "    {",
    "        get",
    "        {",
    '            resourceMan ??= new global::System.Resources.ResourceManager("NextToggle.Properties.Resources", typeof(Resources).Assembly);',
    "            return resourceMan;",
    "        }",
    "    }",
    "",
    "    public static CultureInfo? Culture { get => resourceCulture; set => resourceCulture = value; }",
    "",
]
for k in keys:
    prop = re.sub(r"[^a-zA-Z0-9_]", "_", k)
    lines.append(f'    public static string {prop} => ResourceManager.GetString("{k}", resourceCulture)!;')
lines.append("}")
OUT.write_text("\n".join(lines) + "\n", encoding="utf-8")
print(f"Wrote {len(keys)} properties to {OUT}")
