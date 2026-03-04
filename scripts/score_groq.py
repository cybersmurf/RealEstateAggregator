import json, re, sys, unicodedata

def strip_diacritics(text: str) -> str:
    return ''.join(c for c in unicodedata.normalize('NFD', text) if unicodedata.category(c) != 'Mn')

fn = sys.argv[1] if len(sys.argv) > 1 else '/tmp/groq_result2.json'
text = json.load(open(fn)).get('markdownContent', '')
print(f"CHARS: {len(text)}\n---")
print(text[:2500])
print("\n=== BENCHMARK SCORING ===")

TOPICS = [
    ("Predani 2027",            r"predani.*2027|2027.*predani|brezen.*2027|predani.{0,30}rok"),
    ("Stodola/hospodar",        r"stodol|hospodar"),
    ("Rozbor vody",             r"laboratorni|rozbor.*vod|studnicni.*vod|voda.{0,30}analyz"),
    ("Smluvni pokuta",          r"smluvni.{0,5}pokut|pokuta.{0,30}prodleni"),
    ("Rohova parcela",          r"rohov.{0,10}parcel|rohov.{0,10}pozemek"),
    ("Hodnota po rekonstrukci", r"hodnota.{0,20}po rekonstrukci|po rekonstrukci.{0,30}\d"),
    ("Yield",                   r"yield|hrub.{0,10}vynos|hrub.{0,10}vyno|odhadovany najem|rocni najem"),
    ("Bodovaci tabulka X/5",    r"\d\s*/\s*5\b|SKORE|HODNOCENI.*bod|celkov.{0,10}hodnoceni"),
    ("ROI/navratnost",          r"navratnost|ROI|return"),
    ("Otazky/DD",               r"otazky|co.{0,10}zeptat|Due Diligence|co poverit|doporucujeme"),
    ("Vady (bullets)",          r"rizika|VADY|PROBLEMY"),
    ("Max nabid. cena Kc",      r"[3-4]\s*[0-9]{2,3}\s*[0-9]{3}|maximalni.*cena|nabidnout"),
    ("PENB chybi",              r"PENB.{0,30}chybi|chybi.{0,30}PENB|PENB.*kriti|energetick.{0,20}prukaz"),
    ("Povodnove riziko",        r"povodnov|povoden|zaplaveni"),
    ("Srovnani Kc/m2",          r"trzni.{0,50}Kc|trzne.{0,50}Kc|prumer.{0,20}Kc/m|trzni.{0,30}obvyklou|trzne.{0,30}obvyklou"),
]

score = 0
text_norm = strip_diacritics(text)   # ASCII regex patterny vs. správná česká diakritika
for name, pat in TOPICS:
    m = bool(re.search(pat, text_norm, re.IGNORECASE))
    print(f"{'✅' if m else '❌'} {name}")
    score += m

CZ_DIACRITICS = set("áčďéěíňóřšťúůýžÁČĎÉĚÍŇÓŘŠŤÚŮÝŽ")
alpha = [c for c in text if c.isalpha()]
cz_ratio = sum(1 for c in alpha if c in CZ_DIACRITICS) / max(len(alpha), 1)
print(f"{'✅' if cz_ratio > 0.03 else '❌'} Diakritika ({cz_ratio:.1%})")
score += cz_ratio > 0.03

CZ_WORDS = ["nemovitost","prodej","cena","plocha","pozemek","rekonstrukc","stav","lokac","dispozic","koupeln"]
cz_hit = sum(1 for w in CZ_WORDS if w.lower() in text.lower())
print(f"{'✅' if cz_hit >= 4 else '❌'} Ceska slovni zasoba ({cz_hit} slov)")
score += cz_hit >= 4

EN = re.compile(r"\b(the property|this house|would |however,|furthermore|in conclusion|overall,|I recommend)", re.I)
no_en = not bool(EN.search(text))
print(f"{'✅' if no_en else '❌'} Neodpovida anglicky")
score += no_en

print(f"\nSKORE: {score}/18")
