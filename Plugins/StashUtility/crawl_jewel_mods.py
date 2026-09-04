import urllib.request
import re
import json
import html

urls = {
    "Sapphire": "https://poe2db.tw/us/Sapphire",
    "Ruby": "https://poe2db.tw/us/Ruby",
    "Emerald": "https://poe2db.tw/us/Emerald",
    "Diamond": "https://poe2db.tw/us/Diamond",
    "Time-Lost Sapphire": "https://poe2db.tw/us/Time-Lost_Sapphire",
    "Time-Lost Ruby": "https://poe2db.tw/us/Time-Lost_Ruby",
    "Time-Lost Emerald": "https://poe2db.tw/us/Time-Lost_Emerald",
    "Time-Lost Diamond": "https://poe2db.tw/us/Time-Lost_Diamond"
}

headers = {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)'}

all_mods = {}

def clean_text(raw_html):
    # Strip HTML tags
    text = re.sub(r'<.*?>', '', raw_html)
    text = html.unescape(text)
    return ' '.join(text.split())

for cat, url in urls.items():
    print(f"Fetching {cat} from {url}...")
    try:
        req = urllib.request.Request(url, headers=headers)
        with urllib.request.urlopen(req) as resp:
            content = resp.read().decode('utf-8')
            
        # Match ModsView JSON object
        match = re.search(r'new\s+ModsView\(\s*(\{.*?\}\s*)\);', content, re.DOTALL)
        if not match:
            print(f"ModsView JSON not found for {cat}")
            continue

        json_str = match.group(1)
        data = json.loads(json_str)

        normal_mods = data.get("normal", [])
        count = 0
        for m in normal_mods:
            mod_gen = str(m.get("ModGenerationTypeID", ""))
            mod_type = "Prefix" if mod_gen == "1" else ("Suffix" if mod_gen == "2" else "")
            if not mod_type:
                continue

            name = m.get("Name", "")
            raw_str = m.get("str", "")
            clean_str = clean_text(raw_str)

            if not clean_str:
                continue

            # Parse min and max roll if available
            min_roll = 0.0
            max_roll = 0.0
            val_match = re.search(r'\((\d+(?:\.\d+)?)(?:<span class="ndash">—</span>|—|-)(\d+(?:\.\d+)?)\)', raw_str)
            if val_match:
                min_roll = float(val_match.group(1))
                max_roll = float(val_match.group(2))
            else:
                single_val = re.search(r'\((\d+(?:\.\d+)?)\)', raw_str)
                if single_val:
                    min_roll = float(single_val.group(1))
                    max_roll = min_roll

            mod_id = f"Jewel_{cat}_{name}_{mod_type}".replace(" ", "_")
            if mod_id not in all_mods:
                all_mods[mod_id] = {
                    "Id": mod_id,
                    "Name": f"{name} ({mod_type}): {clean_str}",
                    "Type": mod_type,
                    "Category": cat,
                    "MinRoll": min_roll,
                    "MaxRoll": max_roll
                }
                count += 1

        print(f"Found {count} base mods for {cat}.")

    except Exception as e:
        print(f"Error fetching {cat}: {e}")

print(f"Total unique jewel mods scraped: {len(all_mods)}")

output_path = "c:/Users/Zhu Xian/source/repos/GameHelper2/Plugins/StashUtility/Data/jewel_mod_ranges.json"
with open(output_path, "w", encoding="utf-8") as f:
    json.dump(list(all_mods.values()), f, indent=2, ensure_ascii=False)

print(f"Saved to {output_path}")
