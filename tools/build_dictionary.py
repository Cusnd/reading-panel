"""Build the offline index from the ECDICT 1.0.28 enhanced SQLite release."""

import argparse
from contextlib import closing
import hashlib
import os
from pathlib import Path
import re
import sqlite3
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
URL = "https://github.com/skywind3000/ECDICT/releases/download/1.0.28/ecdict-sqlite-28.zip"
SHA256 = "ea01f76a3b3351021ce47077e89234465cc9441c8793054495320d06c0c3f3f6"
ENTRY_COUNT = 3_402_564
INDEX_VERSION = 29
# Some entries (e.g. "are") name their lemma only in a grammatical definition.
FORM_REFERENCE = re.compile(
    r"(?:^|[（(])[ \t]*(?:[a-z]+\.[ \t]*)?([a-z]+(?:[-'][a-z]+)*)[ \t]*的[ \t]*"
    r"(?:过去式|过去时|过去分词|现在式|现在时|现在分词|第[一二三]人称|单数|复数|比较级|最高级|ing形式)",
    re.IGNORECASE | re.MULTILINE | re.ASCII,
)
NOUN_LABEL = re.compile(r"^(?:n|pl)\.", re.IGNORECASE | re.MULTILINE)


def is_current(path):
    if not path.is_file():
        return False
    try:
        with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)) as db:
            return db.execute("PRAGMA user_version").fetchone() == (INDEX_VERSION,)
    except sqlite3.DatabaseError:
        return False


def inflections(db):
    rows = db.execute("""
        SELECT word, coalesce(exchange, ''), coalesce(pos, ''),
               coalesce(nullif(trim(translation), ''), trim(definition), '') FROM upstream.stardict
        WHERE exchange != '' OR instr(translation, '的') > 0
    """)
    for word, exchange, pos, meaning in rows:
        if not any(c.isspace() for c in word):
            for lemma in FORM_REFERENCE.findall(meaning):
                if lemma.casefold() != word.casefold():
                    yield word, lemma, 0
        for field in exchange.split("/"):
            kind, _, value = field.partition(":")
            if not value or value.casefold() == word.casefold():
                continue
            if kind == "0":
                yield word, value, 1  # Lemma named by the inflected entry itself.
            elif kind in {"p", "d", "i", "3", "r", "t", "s"}:
                # ECDICT also lists automatic plurals for names and non-nouns (e.g. I -> is).
                fallback_only = kind == "s" and not NOUN_LABEL.search(meaning) and not any(
                    tag.startswith("n:") for tag in pos.split("/")
                )
                yield value, word, 3 if fallback_only else 2


def build(archive, target):
    digest = hashlib.sha256()
    with archive.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    if digest.hexdigest() != SHA256:
        raise ValueError("Checksum does not match the ECDICT 1.0.28 SQLite release")
    with zipfile.ZipFile(archive) as package:
        source_db = Path(package.extract("stardict.db", target.parent))
    with closing(sqlite3.connect(target, uri=True)) as db:
        db.execute("ATTACH DATABASE ? AS upstream", (source_db.as_uri() + "?mode=ro",))
        db.executescript("""
          CREATE TABLE words (word TEXT PRIMARY KEY COLLATE NOCASE, phonetic TEXT NOT NULL,
                              meaning TEXT NOT NULL, tag TEXT NOT NULL) WITHOUT ROWID;
          CREATE TABLE forms (form TEXT COLLATE NOCASE NOT NULL, lemma TEXT COLLATE NOCASE NOT NULL,
                              priority INTEGER NOT NULL, PRIMARY KEY (form, lemma)) WITHOUT ROWID;
          INSERT INTO words
            SELECT word, coalesce(phonetic, ''), coalesce(nullif(trim(translation), ''), trim(definition)), coalesce(tag, '')
            FROM upstream.stardict ORDER BY word;
        """)
        db.executemany("""
            INSERT INTO forms VALUES (?,?,?)
            ON CONFLICT (form, lemma) DO UPDATE SET priority = excluded.priority
            WHERE excluded.priority < forms.priority
        """, inflections(db))
        db.execute("""
            DELETE FROM forms
            WHERE NOT EXISTS (SELECT 1 FROM words WHERE words.word = forms.lemma)
               OR (priority = 3 AND EXISTS (SELECT 1 FROM words WHERE words.word = forms.form))
        """)
        count = db.execute("SELECT count(*) FROM words").fetchone()[0]
        if count != ENTRY_COUNT or db.execute("PRAGMA quick_check").fetchone() != ("ok",):
            raise ValueError("Incomplete dictionary")
        db.execute(f"PRAGMA user_version = {INDEX_VERSION}")
        db.commit()
    return count


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    action = parser.add_mutually_exclusive_group()
    action.add_argument("--archive", type=Path, help="Build from a local ecdict-sqlite-28.zip")
    action.add_argument("--compact-copy", type=Path, metavar="FILE",
                        help="Repack the existing index for distribution without changing its entries")
    args = parser.parse_args()
    output = ROOT / "assets" / "ecdict.sqlite3"
    if args.compact_copy is not None:
        destination = args.compact_copy.resolve()
        if destination == output:
            parser.error("--compact-copy needs a separate destination")
        if not is_current(output):
            parser.error("build the enhanced offline index before repacking it")
        with tempfile.TemporaryDirectory(dir=destination.parent) as staging:
            packed = Path(staging) / "dictionary.sqlite3"
            with closing(sqlite3.connect(output.as_uri() + "?mode=ro", uri=True)) as db:
                db.execute("PRAGMA page_size=16384")
                db.execute("VACUUM INTO ?", (str(packed),))
            os.replace(packed, destination)
        return
    if args.archive is None and is_current(output):
        return
    output.parent.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output.parent) as staging:
        source = args.archive
        if source is None:
            source = Path(staging) / "ecdict-sqlite-28.zip"
            urllib.request.urlretrieve(URL, source)
        temporary = Path(staging) / "dictionary.sqlite3"
        count = build(source, temporary)
        os.replace(temporary, output)
    print(f"{count:,} dictionary entries in {output}")


if __name__ == "__main__":
    main()
