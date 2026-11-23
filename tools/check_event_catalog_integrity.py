"""Check repository and bundled catalog integrity without touching live data."""
from pathlib import Path
import argparse
import subprocess
from event_catalog import ID, parse, read_catalog, validate_title


def inventory(root):
    data = read_catalog(root)
    stats = parse((root / "Stats.json").read_bytes())
    supported = list(map(str, data.pop("SupportedTitleIDs")))
    if len(set(supported)) != len(supported) or set(supported) - data.keys():
        raise ValueError(f"{root}: duplicate or unknown supported title")
    for tid, title in data.items():
        validate_title(title)
        if "TitleId" in title and str(title["TitleId"]) != tid:
            raise ValueError(f"{root}/{tid}: wrong TitleId")
    for path in root.glob("*/Data.json"):
        if path.parent.name not in data:
            raise ValueError(f"{path}: title absent from index")
    for path in root.glob("*/Stats.json"):
        tid = path.parent.name
        wrapper = parse(path.read_bytes())
        if list(wrapper) != [tid, "SupportedTitleIDs"] or list(map(str, wrapper["SupportedTitleIDs"])) != [tid]:
            raise ValueError(f"{path}: invalid title wrapper")
        if tid not in stats or wrapper[tid] != stats[tid]:
            raise ValueError(f"{path}: disagrees with combined stats")
    for tid, block in stats.items():
        if not ID.fullmatch(tid) or not isinstance(block.get("Stats"), dict):
            raise ValueError(f"{root}/Stats.json: invalid title {tid}")
        if not (root / tid / "Stats.json").is_file():
            raise ValueError(f"{root}/{tid}: missing Stats.json")
    return data, stats, set(supported)


def previous(base, path):
    return parse(subprocess.check_output(["git", "show", f"{base}:{path}"]))


def known_mapping_correction(tid, old, current):
    """Recognize the exact reviewed corrections, without allowing other removals."""
    if tid == "1050025307":
        # GTA V's event recipes were keyed one higher than the Xbox IDs after 49.
        corrected = {str(int(key) - 1) if int(key) >= 51 else key: recipe
                     for key, recipe in old.items()}
        return len(corrected) == len(old) and current == corrected
    if tid == "1285166951" and "107" in old:
        # The old Imperial Lightbringer recipe actually unlocked Horror of Horrors.
        corrected = {key: recipe for key, recipe in old.items() if key != "107"}
        corrected["109"] = old["107"]
        return current == corrected
    return False


def reject_removals(base, data, stats, supported):
    old_index = previous(base, "Events/index.json")
    old_stats = previous(base, "Events/Stats.json")
    removed = set(map(str, old_index.pop("SupportedTitleIDs"))) - supported
    removed.update(old_index.keys() - data.keys())
    removed.update(old_stats.keys() - stats.keys())
    if removed:
        raise ValueError(f"Removed catalog titles: {sorted(removed)}")
    changed = subprocess.check_output(
        ["git", "diff", "--name-only", "-z", base, "--", "Events"], text=True).split("\0")
    changed_achievement_titles = {Path(path).parent.name for path in changed
                                  if path.endswith("/Data.json")}
    for tid in sorted(changed_achievement_titles & old_index.keys()):
        old = previous(base, f"Events/{tid}/Data.json")[tid]["Achievements"]
        current = data[tid]["Achievements"]
        if old.keys() - current.keys() and not known_mapping_correction(tid, old, current):
            raise ValueError(f"{tid}: removed achievement mappings")
    for tid, block in old_stats.items():
        mapped = {name for name, item in block.get("Stats", {}).items()
                  if isinstance(item, dict) and any(k.startswith("Request") for k in item)}
        current = stats[tid]["Stats"]
        if any(not any(k.startswith("Request") for k in current.get(name, {})) for name in mapped):
            raise ValueError(f"{tid}: removed stat recipes")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--base")
    args = parser.parse_args()
    public = inventory(Path("Events"))
    bundled = inventory(Path("src/AchievementLabs.App/Events"))
    if public != bundled:
        raise ValueError("Public and bundled catalog data differ")
    if args.base:
        reject_removals(args.base, *public)
    print(f"Catalog integrity passed for {len(public[0])} achievement titles and {len(public[1])} stat titles.")
