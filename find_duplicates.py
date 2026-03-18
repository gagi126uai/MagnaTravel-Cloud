import re
from collections import Counter

file_path = r"d:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelWeb\src\pages\SettingsPage.jsx"

try:
    with open(file_path, "r", encoding="utf-8") as f:
        content = f.read()

    # Find all import statements
    # Simple regex for import ... ;
    imports = re.findall(r"^import\s+.*?;", content, re.MULTILINE | re.DOTALL)

    print(f"Total import statements: {len(imports)}")

    # Check for duplicate identifiers in all { ... } blocks
    all_identifiers = []
    for imp in imports:
        match = re.search(r"\{(.*?)\}", imp, re.S)
        if match:
            # Handle multiple identifiers in one { ... }
            ids = [i.strip() for i in match.group(1).replace("\n", " ").split(",") if i.strip()]
            all_identifiers.extend(ids)

    counts = Counter(all_identifiers)
    duplicates = {k: v for k, v in counts.items() if v > 1}

    if duplicates:
        print("Duplicate identifiers found:")
        for k, v in duplicates.items():
            print(f"  {k}: {v} times")
    else:
        print("No duplicate identifiers found in { ... } blocks.")

except Exception as e:
    print(f"Error: {e}")
