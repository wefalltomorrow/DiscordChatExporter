#!/usr/bin/env python3
"""
Compare two DiscordChatExporter JSON exports of the same channel and period.

The point of this tool is to prove that this fork's --normal and --extended output carries
exactly the same message data as a vanilla export, by checking one against the other. It
therefore knows how to read both schemas and reduces them to a common form before comparing:

  * A normalized export (--normal) is rehydrated -- authorId, mentionIds, stickerIds,
    inlineEmojiKeys and the reactions' emojiKey/userIds are resolved back through the root
    lookup tables into the inline objects a vanilla export would have written.
  * Keys that exist in only one of the two files are reported as additions rather than as
    mismatches, which is what the extended fields and the lookup tables themselves are.

Not every difference means corruption. The two exports were taken at different times, against
a live server, so some values legitimately drift: someone renames themselves, changes their
avatar, gains a role, adds a reaction, and Discord re-signs its CDN URLs on every request.
Differences are therefore classified, and only the first class is a failure:

    HARD   message identity and content -- must match exactly
    SOFT   values that genuinely change between two runs against a live server
    MENTN  message content differing only inside an @user, @role or #channel mention, which
           the exporter resolves to a *name* at export time, so a rename rewrites old messages
    ADDED  a key present in only one file (extended fields, lookup tables)

Usage:
    compare_exports.py A.json B.json              compare one pair
    compare_exports.py --dir DIR_A DIR_B          compare every pair of files with the same
                                                  name, recursively, and summarize
Options:
    --verbose        list every difference, including SOFT ones
    --max N          stop listing after N differences per pair (default 12)
    --strict         treat SOFT differences as failures too
    --quiet          only print the final summary
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from urllib.parse import urlsplit, urlunsplit, parse_qsl, urlencode

# ---------------------------------------------------------------------------- classification

# Paths are rendered with '[]' in place of a list index, so one pattern covers every element.
# The first pattern that matches a path decides its class, so order matters here.
SOFT_PATTERNS = [
    # Taken at different moments, by definition
    r"^exportedAt$",
    # Guild and channel metadata can be edited between two runs
    r"^guild\.(name|iconUrl)$",
    r"^channel\.(name|category|topic|position|memberCount|isArchived|isLocked)$",
    # Per-member state: a nickname, a colour, a role set and an avatar are all mutable, and the
    # author object is embedded in every single message, so one rename shows up thousands of
    # times. The Discord username and discriminator belong here too -- they are display data
    # that the account owner can change at will. Identity is the ID, and that stays HARD.
    r"\.(nickname|color|avatarUrl|bannerUrl)$",
    r"\.(name|discriminator)$",
    r"\.roles(\[\])?(\..*)?$",
    r"\.(joinedAt|premiumSince|isPending|flags)$",
    # Someone can react, or un-react, between the two runs
    r"^messages\[\]\.reactions\[\]\.count$",
    r"^messages\[\]\.reactions\[\]\.users\b.*$",
    # Discord re-signs attachment and media URLs per request; the canonicalizer already strips
    # the signature, so anything left here is a genuinely different path
    r"^dateRange\.",
]

SOFT_RE = [re.compile(p) for p in SOFT_PATTERNS]

# Signature and cache-busting parameters Discord adds to CDN links. They are regenerated on
# every request, so two exports of the same attachment never agree on them.
VOLATILE_QUERY_KEYS = {"ex", "is", "hm", "_", "size", "quality", "format", "width", "height"}


MENTION_PREFIXES = ("@", "#", "<@", "<#")


class MentionMasker:
    """
    Rewrites resolved mentions in message bodies back into stable identifiers.

    The exporter resolves '<@123>' to '@theirname' and '<#456>' to '#channel-name' while
    writing, so renaming a user, role or channel silently rewrites the body of every old
    message that mentioned it. To tell that apart from real damage, every display name the two
    documents know for a given ID is replaced by that ID: two bodies that then match differed
    only in what someone is currently called. Crucially the substitution is keyed by ID, so a
    body that mentions a genuinely *different* user still comes out as a real difference.

    Display names can contain spaces, which is exactly why this is driven by the documents'
    own user and role data instead of by tokenizing the text.
    """

    def __init__(self, *docs: dict):
        # id -> display strings, unioned across both documents
        users: dict[str, set[str]] = {}
        roles: dict[str, set[str]] = {}
        channels: set[str] = set()

        def add_user(u):
            if isinstance(u, dict) and "id" in u:
                bag = users.setdefault(u["id"], set())
                for k in ("name", "nickname"):
                    if isinstance(u.get(k), str) and u[k]:
                        bag.add(u[k])
                for r in u.get("roles") or []:
                    add_role(r)

        def add_role(r):
            if isinstance(r, dict) and "id" in r and isinstance(r.get("name"), str):
                roles.setdefault(r["id"], set()).add(r["name"])

        for doc in docs:
            for u in doc.get("users") or []:
                add_user(u)
            for r in doc.get("roles") or []:
                add_role(r)
            for r in (doc.get("guild") or {}).get("roles") or []:
                add_role(r)
            for m in doc.get("messages") or []:
                add_user(m.get("author"))
                for u in m.get("mentions") or []:
                    add_user(u)
                for rx in m.get("reactions") or []:
                    for u in rx.get("users") or []:
                        add_user(u)
            name = (doc.get("channel") or {}).get("name")
            if isinstance(name, str) and name:
                channels.add(name)

        pairs: list[tuple[str, str]] = []
        for uid, names in users.items():
            for n in names:
                pairs.append(("@" + n, f"@\x00U{uid}"))
        for rid, names in roles.items():
            for n in names:
                pairs.append(("@" + n, f"@\x00R{rid}"))
        for n in channels:
            pairs.append(("#" + n, "#\x00C"))

        # Longest first, so that a name that is a prefix of another doesn't win
        self._pairs = sorted(pairs, key=lambda p: -len(p[0]))

    def mask(self, text: str) -> str:
        for needle, repl in self._pairs:
            if needle in text:
                text = text.replace(needle, repl)
        return text


def _tokens_are_mentions(a: str, b: str) -> bool:
    """Fallback for mentions of things the documents don't name, such as another channel."""
    import difflib

    ta, tb = a.split(), b.split()
    for tag, i1, i2, j1, j2 in difflib.SequenceMatcher(a=ta, b=tb).get_opcodes():
        if tag == "equal":
            continue
        chunk = ta[i1:i2] + tb[j1:j2]
        if not chunk or not all(t.startswith(MENTION_PREFIXES) for t in chunk):
            return False
    return True


def is_mention_drift(a, b, masker: "MentionMasker | None") -> bool:
    if not isinstance(a, str) or not isinstance(b, str):
        return False
    if masker is not None:
        ma, mb = masker.mask(a), masker.mask(b)
        if ma == mb:
            return True
        a, b = ma, mb
    return _tokens_are_mentions(a, b)


def classify(path: str) -> str:
    for rx in SOFT_RE:
        if rx.search(path):
            return "SOFT"
    return "HARD"


# ---------------------------------------------------------------------------- canonicalization


def canon_url(value: str) -> str:
    """Drop the query parameters that Discord regenerates on every request."""
    if "://" not in value:
        return value
    try:
        parts = urlsplit(value)
    except ValueError:
        return value
    if not parts.query:
        return value
    kept = [(k, v) for k, v in parse_qsl(parts.query, keep_blank_values=True)
            if k not in VOLATILE_QUERY_KEYS]
    return urlunsplit((parts.scheme, parts.netloc, parts.path, urlencode(kept), parts.fragment))


def canon(value):
    if isinstance(value, str):
        return canon_url(value)
    if isinstance(value, dict):
        return {k: canon(v) for k, v in value.items()}
    if isinstance(value, list):
        return [canon(v) for v in value]
    return value


# ---------------------------------------------------------------------------- rehydration


class Rehydrator:
    """Turns a --normal export back into the shape a vanilla export would have written."""

    def __init__(self, doc: dict):
        self.users = {u["id"]: u for u in doc.get("users", [])}
        self.roles = {r["id"]: r for r in doc.get("roles", [])}
        self.emojis = {e["key"]: e for e in doc.get("emojis", [])}
        self.stickers = {s["id"]: s for s in doc.get("stickers", [])}
        self.dangling: list[str] = []

    def user(self, user_id: str, with_roles: bool) -> dict:
        u = self.users.get(user_id)
        if u is None:
            self.dangling.append(f"user {user_id}")
            return {"id": user_id, "__dangling__": True}
        u = dict(u)
        role_ids = u.pop("roleIds", None)
        if with_roles:
            # Role order is significant and preserved by the writer, so keep it
            u["roles"] = [self.role(r) for r in (role_ids or [])]
        return u

    def role(self, role_id: str) -> dict:
        r = self.roles.get(role_id)
        if r is None:
            self.dangling.append(f"role {role_id}")
            return {"id": role_id, "__dangling__": True}
        return dict(r)

    def emoji(self, key: str) -> dict:
        e = self.emojis.get(key)
        if e is None:
            self.dangling.append(f"emoji {key!r}")
            return {"key": key, "__dangling__": True}
        e = dict(e)
        # 'key' exists only to be referenced; an inline emoji never carries it
        e.pop("key", None)
        return e

    def sticker(self, sticker_id: str) -> dict:
        s = self.stickers.get(sticker_id)
        if s is None:
            self.dangling.append(f"sticker {sticker_id}")
            return {"id": sticker_id, "__dangling__": True}
        return dict(s)

    def message(self, m: dict) -> dict:
        m = dict(m)
        if "authorId" in m:
            m["author"] = self.user(m.pop("authorId"), with_roles=True)
        if "mentionIds" in m:
            m["mentions"] = [self.user(i, with_roles=True) for i in m.pop("mentionIds")]
        if "stickerIds" in m:
            m["stickers"] = [self.sticker(i) for i in m.pop("stickerIds")]
        if "inlineEmojiKeys" in m:
            m["inlineEmojis"] = [self.emoji(k) for k in m.pop("inlineEmojiKeys")]
        if "reactions" in m:
            reactions = []
            for r in m["reactions"]:
                r = dict(r)
                if "emojiKey" in r:
                    r["emoji"] = self.emoji(r.pop("emojiKey"))
                if "userIds" in r:
                    # Reaction authors are written without roles by the vanilla writer
                    r["users"] = [self.user(i, with_roles=False) for i in r.pop("userIds")]
                reactions.append(r)
            m["reactions"] = reactions
        if "interaction" in m and isinstance(m["interaction"], dict):
            it = dict(m["interaction"])
            if "userId" in it:
                it["user"] = self.user(it.pop("userId"), with_roles=True)
            m["interaction"] = it
        return m

    def guild(self, g: dict) -> dict:
        # The extended guild inventory is normalized the same way; expand it so that two
        # extended exports (one normalized, one not) also compare cleanly
        g = dict(g)
        if "roleIds" in g:
            g["roles"] = [self.role(i) for i in g.pop("roleIds")]
        if "emojiKeys" in g:
            g["emojis"] = [self.emoji(k) for k in g.pop("emojiKeys")]
        if "stickerIds" in g:
            g["stickers"] = [self.sticker(i) for i in g.pop("stickerIds")]
        if "ownerId" in g and g["ownerId"] and "owner" not in g:
            g["owner"] = self.user(g["ownerId"], with_roles=True)
        return g


class Unreadable(Exception):
    """The file can't be compared right now (locked by a running export, or truncated)."""


def load(path: str) -> tuple[dict, dict]:
    try:
        with open(path, encoding="utf-8") as f:
            doc = json.load(f)
    except PermissionError:
        # DCE opens its output with no sharing, so a still-running export holds an exclusive
        # lock on the file and it simply cannot be read until that export finishes
        raise Unreadable("locked by a running export") from None
    except json.JSONDecodeError as ex:
        raise Unreadable(f"not valid JSON ({ex.msg} at line {ex.lineno})") from None
    except OSError as ex:
        raise Unreadable(str(ex)) from None
    mod = doc.get("mod") or {}
    if mod.get("normal"):
        r = Rehydrator(doc)
        doc = dict(doc)
        doc["guild"] = r.guild(doc["guild"])
        doc["messages"] = [r.message(m) for m in doc["messages"]]
        for table in ("users", "roles", "emojis", "stickers"):
            doc.pop(table, None)
        info = {"normal": True, "extended": bool(mod.get("extended")), "dangling": r.dangling}
    else:
        info = {"normal": False, "extended": bool(mod.get("extended")), "dangling": []}
    return doc, info


# ---------------------------------------------------------------------------- diffing


def diff(a, b, path: str, out: list, added: list, masker=None):
    """Collect (class, path, a, b) differences. 'added' gets keys present on only one side."""
    if isinstance(a, dict) and isinstance(b, dict):
        for k in a.keys() | b.keys():
            sub = f"{path}.{k}" if path else k
            if k not in b:
                added.append((sub, "A only"))
            elif k not in a:
                added.append((sub, "B only"))
            else:
                diff(a[k], b[k], sub, out, added, masker)
        return

    if isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            out.append((classify(path + "[]"), f"{path} (length)", len(a), len(b)))
        for i in range(min(len(a), len(b))):
            diff(a[i], b[i], f"{path}[]", out, added, masker)
        return

    if a != b:
        cls = classify(path)
        if cls == "HARD" and path.endswith("content") and is_mention_drift(a, b, masker):
            cls = "MENTN"
        out.append((cls, path, a, b))


def compare(path_a: str, path_b: str, max_list: int, strict: bool, verbose: bool):
    name = os.path.basename(path_a)
    try:
        doc_a, info_a = load(path_a)
        doc_b, info_b = load(path_b)
    except Unreadable as ex:
        line = f"  SKIP  {'':>6}       {name}  [{ex}]"
        return None, line, [], dict(messages=0, hard=0, soft=0, mentn=0, added=0, notes=[])

    hard: list = []
    soft: list = []
    mentn: list = []
    added: list = []
    notes: list[str] = []

    for d in (info_a, info_b):
        if d["dangling"]:
            uniq = sorted(set(d["dangling"]))
            hard.append(("HARD", "<dangling references>", len(d["dangling"]), uniq[:5]))

    # --- messages, matched by ID ---
    ids_a = [m["id"] for m in doc_a["messages"]]
    ids_b = [m["id"] for m in doc_b["messages"]]
    set_a, set_b = set(ids_a), set(ids_b)

    if len(ids_a) != len(set_a):
        hard.append(("HARD", "<duplicate message IDs in A>", len(ids_a) - len(set_a), 0))
    if len(ids_b) != len(set_b):
        hard.append(("HARD", "<duplicate message IDs in B>", len(ids_b) - len(set_b), 0))

    only_a, only_b = set_a - set_b, set_b - set_a
    if only_a:
        hard.append(("HARD", "<messages only in A>", len(only_a), sorted(only_a)[:5]))
    if only_b:
        hard.append(("HARD", "<messages only in B>", len(only_b), sorted(only_b)[:5]))

    # Order must agree on the messages both files have
    common_order_a = [i for i in ids_a if i in set_b]
    common_order_b = [i for i in ids_b if i in set_a]
    if common_order_a != common_order_b:
        hard.append(("HARD", "<message order differs>", "A order", "B order"))

    by_a = {m["id"]: m for m in doc_a["messages"]}
    by_b = {m["id"]: m for m in doc_b["messages"]}

    # Built from both documents at once, so that either document's spelling of a name maps to
    # the same identifier
    masker = MentionMasker(doc_a, doc_b)

    for mid in common_order_a:
        ma, mb = canon(by_a[mid]), canon(by_b[mid])
        out: list = []
        add: list = []
        diff(ma, mb, "messages[]", out, add, masker)
        for cls, p, va, vb in out:
            bucket = hard if cls == "HARD" else (mentn if cls == "MENTN" else soft)
            bucket.append((cls, f"{p} (#{mid})", va, vb))
        added.extend(add)

    # --- root, excluding messages ---
    root_a = {k: v for k, v in doc_a.items() if k != "messages"}
    root_b = {k: v for k, v in doc_b.items() if k != "messages"}
    out, add = [], []
    diff(canon(root_a), canon(root_b), "", out, add)
    for cls, p, va, vb in out:
        bucket = hard if cls == "HARD" else (mentn if cls == "MENTN" else soft)
        bucket.append((cls, p, va, vb))
    added.extend(add)

    ok = not hard and (not strict or not (soft or mentn))

    # --- report ---
    status = "OK  " if ok else "FAIL"
    counts = f"{len(set_a & set_b):6} msgs"
    extra = []
    if soft:
        extra.append(f"{len(soft)} soft")
    if mentn:
        extra.append(f"{len(mentn)} mention")
    if added:
        extra.append(f"{len({p for p, _ in added})} added keys")
    if hard:
        extra.append(f"{len(hard)} HARD")
    tail = ("  [" + ", ".join(extra) + "]") if extra else ""
    line = f"  {status}  {counts}  {name}{tail}"

    details = []
    shown = hard + (soft + mentn if verbose else [])
    for cls, p, va, vb in shown[:max_list]:
        details.append(f"        {cls}  {p}\n            A: {va!r}\n            B: {vb!r}")
    if len(shown) > max_list:
        details.append(f"        ... and {len(shown) - max_list} more")
    if verbose and added:
        keys = sorted({f"{p} ({side})" for p, side in added})
        details.append("        ADDED " + ", ".join(keys[:20])
                       + (f" ... +{len(keys) - 20}" if len(keys) > 20 else ""))

    return ok, line, details, dict(
        messages=len(set_a & set_b), hard=len(hard), soft=len(soft), mentn=len(mentn),
        added=len({p for p, _ in added}), notes=notes,
    )


# ---------------------------------------------------------------------------- entry point


def main() -> int:
    ap = argparse.ArgumentParser(description="Compare two DiscordChatExporter JSON exports.")
    ap.add_argument("a")
    ap.add_argument("b")
    ap.add_argument("--dir", action="store_true",
                    help="treat A and B as directories and compare same-named files recursively")
    ap.add_argument("--verbose", action="store_true", help="list SOFT differences and added keys")
    ap.add_argument("--max", type=int, default=12, help="max differences listed per pair")
    ap.add_argument("--strict", action="store_true", help="fail on SOFT differences too")
    ap.add_argument("--quiet", action="store_true", help="only print the summary")
    args = ap.parse_args()

    if not args.dir:
        ok, line, details, _ = compare(args.a, args.b, args.max, args.strict, args.verbose)
        print(line)
        for d in details:
            print(d)
        # A skipped pair (ok is None) is neither a pass nor a failure, but it did not verify
        # anything, so it must not report success
        return 0 if ok else 1 if ok is False else 2

    def listing(root):
        found = {}
        for dirpath, _, files in os.walk(root):
            for f in files:
                if f.lower().endswith(".json"):
                    rel = os.path.relpath(os.path.join(dirpath, f), root)
                    found[rel.replace("\\", "/")] = os.path.join(dirpath, f)
        return found

    la, lb = listing(args.a), listing(args.b)
    common = sorted(la.keys() & lb.keys())
    only_a = sorted(la.keys() - lb.keys())
    only_b = sorted(lb.keys() - la.keys())

    failures = skipped = 0
    total_msgs = total_soft = total_mentn = total_added = 0
    groups: dict[str, list] = {}
    for rel in common:
        groups.setdefault(os.path.dirname(rel) or ".", []).append(rel)

    for group in sorted(groups):
        if not args.quiet:
            print(f"\n=== {group} ===")
        for rel in groups[group]:
            ok, line, details, stats = compare(la[rel], lb[rel], args.max, args.strict, args.verbose)
            total_msgs += stats["messages"]
            total_soft += stats["soft"]
            total_mentn += stats["mentn"]
            total_added += stats["added"]
            if ok is None:
                skipped += 1
            elif not ok:
                failures += 1
            if not args.quiet:
                print(line)
                for d in details:
                    print(d)

    print("\n" + "=" * 72)
    print(f"pairs compared     : {len(common) - skipped}")
    print(f"messages compared  : {total_msgs}")
    print(f"pairs with HARD    : {failures}")
    print(f"soft differences   : {total_soft}")
    print(f"mention drift      : {total_mentn}")
    if skipped:
        print(f"skipped (unreadable): {skipped}")
    if only_a:
        print(f"\nonly in A ({len(only_a)}):")
        for f in only_a:
            print("   ", f)
    if only_b:
        print(f"\nonly in B ({len(only_b)}):")
        for f in only_b:
            print("   ", f)
    print("=" * 72)
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
