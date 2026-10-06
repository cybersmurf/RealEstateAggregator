"""
Obce okresů Znojmo a Brno-venkov jako slugy (bez diakritiky, slova spojená pomlčkou).

Stav k 6. 10. 2026 podle kategorií „Obce v okrese …" na cs.wikipedia.org. UlovDomov podle nich
předvybírá adresy ze sitemapy, iDNES ověřuje položky výpisu, u kterých portál okres neuvádí.
Okres inzerátu se z názvu obce nikdy neodvozuje – stejnojmenných obcí je po republice víc.
"""
from typing import Dict, FrozenSet

from .district_lookup import normalize_place

_ZNOJMO_SLUGS = """
bantice beharovice bezkov bitov blanne blizkovice bohutice bojanovice borotice boskovstejn bozice brezany
cejkovice cermakovice cernin chvalatice chvalovice citonice ctidruzice damnice dobelice dobrinsko dobsice
dolenice dolni-dubnany dyjakovice dyjakovicky dyje dzbanice greslove-myto havraniky hevlin hluboke-masuvky
hnanice hodonice horni-breckov horni-dubnany horni-dunajovice horni-kounice hosteradice hostim hrabetice
hradek hrusovany-nad-jevisovkou jamolice jaroslavice jevisovice jezerany-marsovice jirice-u-miroslavi
jirice-u-moravskych-budejovic kadov korolupy kravsko krepice krhovice kridluvky kubsice kucharovice kyjovice
lancov lechovice lesna lesonice litobratrice lubnice lukov mackovice masovice medlice mikulovice milicovice
miroslav miroslavske-kninice morasice moravsky-krumlov nasimerice nemcicky novy-saldorf-sedlesovice
olbramkostel olbramovice oleksovice onsov oslnovice pavlice petrovice plavec plenkovice podhradi-nad-dyji
podmoli podmyce prace pravice preskace prokopov prosimerice resice rozkos rudlice rybniky safov sanov satov
skalice slatina slup stalky stary-petrin stitary stosikovice-na-louce strachotice strelice suchohrdly
suchohrdly-u-miroslavi sumna tasovice tavikovice tesetice trnove-pole trstenice tulesice tvorihraz uhercice
ujezd unanov valtrovice vedrovice velky-karlov vemyslice vevcice visnove vitonice vracovice vranov-nad-dyji
vranovska-ves vratenin vrbovec vyrovice vysocany zalesi zblovice zeletice zerotice zerutky znojmo
"""
_BRNO_VENKOV_SLUGS = """
babice-nad-svitavou babice-u-rosic belec bilovice-nad-svitavou biskoupky blazovice blucina borac borovnik
braniskov branisovice bratcice brezina brumov bukovice cebin cernvir ceska chudcice cucice cvrcovice deblin
dolni-kounice dolni-loucky domasov doubravnik drahonin drasov hajany heroltice hlina hluboke-dvory holasice
horni-loucky hostenice hradcany hrusovany-u-brna hvozdec ivan ivancice javurek jinacovice jirikovice kaly
kanice katov ketkovice kobylnice kovalovice kratochvilka krizinkov kuparovice kurim kurimska-nova-ves
kurimske-jestrabi lazanky ledce lelekovice lesni-hluboke litostrov lodenice lomnice lomnicka lubne lukovany
malesovice malhostovice marsov medlov melcany menin modrice mokra-horakov moravany moravske-branice
moravske-kninice moutnice nebovidy nedvedice nelepec-zernuvka nemcicky neslovice nesvacilka nihov nosislav
nova-ves nove-branice ochoz-u-brna ochoz-u-tisnova odrovice olsi omice opatovice orechov osiky oslavany
ostopovice ostrovacice otmarov pasohlavky pernstejnske-jestrabi podoli pohorelice ponetovice popovice
popuvky pozorice prace pravlov predklasteri pribice pribram-na-morave pribyslavice prisnotice prstice
radostice rajhrad rajhradice rasov rebesovice ricany ricky ricmanice rikonin rohozec rojetin rosice
rozdrojovice rudka senorady sentice serkovice siluvky sivice skalicka skryje slapanice sobotovice sokolnice
stanoviste stepanovice strelice strhare sumice svatoslav synalov syrovice telnice tesany tetcice tisnov
tisnovska-nova-ves trbousany troskotovice troubsko tvarozna ujezd-u-brna ujezd-u-rosic ujezd-u-tisnova unin
unkovice ususi velatice veverska-bityska veverske-kninice vinicne-sumice vlasatice vohancice vojkovice
vranov vranovice vratislavka vsechovice vysoke-popovice zabcice zakrany zalesna-zhor zastavka zatcany
zbraslav zbysov zdarec zelesice zelezne zhor zidlochovice
"""
DISTRICT_MUNICIPALITY_SLUGS: Dict[str, FrozenSet[str]] = {
    "Znojmo": frozenset(_ZNOJMO_SLUGS.split()),
    "Brno-venkov": frozenset(_BRNO_VENKOV_SLUGS.split()),
    "Brno-město": frozenset({"brno"}),
}


def municipality_slug(name: str) -> str:
    """„Hrušovany nad Jevišovkou" → „hrusovany-nad-jevisovkou"."""
    return normalize_place(name).replace(" ", "-")


def is_district_municipality(name: str, district: str) -> bool:
    """Je obec tohoto jména v seznamu obcí okresu?"""
    return municipality_slug(name) in DISTRICT_MUNICIPALITY_SLUGS.get(district, frozenset())
