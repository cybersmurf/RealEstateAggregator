# Loga realitních kanceláří

Tato složka obsahuje loga realitních kanceláří použitá v aplikaci. Loga jsou pojmenována podle `SourceCode` definovaného v `scraper/config/settings.yaml`.

## Seznam log a zdroje

| RK | Soubor | Zdroj |
| :--- | :--- | :--- |
| **RE/MAX** | `REMAX.svg` | https://logo.svgcdn.com/logos/re-max.svg |
| **M&M Reality** | `MMR.png` | https://www.mmreality.cz/img/logo-mm.png |
| **Century 21** | `CENTURY21.svg` | https://www.century21.cz/assets/img/c21-logo.svg |
| **Sreality** | `SREALITY.png` | https://www.sreality.cz/img/sreality-logo.png |
| **Prodejme.to** | `PRODEJMETO.png` | https://www.prodejme.to/img/logo.png |
| **Lexamo** | `LEXAMO.png` | https://www.lexamo.cz/assets/images/logo.png |
| **HV Reality** | `HVREALITY.png` | https://www.hvreality.cz/img/logo.png |
| **Nemovitosti Znojmo** | `NEMZNOJMO.png` | https://www.nemovitostiznojmo.cz/img/logo.png |
| **Premiera Reality** | `PREMIAREALITY.png` | https://www.premiareality.cz/templates/premiareality/images/logo.png |
| **Delux Reality** | `DELUXREALITY.png` | https://www.deluxreality.cz/img/logo.png |
| **Znojmo Reality** | `ZNOJMOREALITY.png` | https://www.znojmoreality.cz/img/logo.png |

## Použití v Blazoru
Loga jsou automaticky mapována v komponentách pomocí `@($"/images/logos/{context.SourceCode}.png")`.
