# Trading Central pivot as a bounce level

Intraday views of EURUSD, GBPUSD, USDCHF, USDJPY, USDCAD, AUDUSD, NZDUSD, EURGBP, 2026-08-16 .. 2026-10-01, one distinct level set = one pivot (357 pivots with price data and ADR). Rules of `market-review/levels.md`: touch = the minute high/low comes within 10% ADR(14) of the level; bounce = after the first touch the price moves 25% ADR away before it goes more than 10% ADR through; break = 25% ADR through; pierced = more than 10% through but less than 25%. Bounce rate = bounce / touched.

Control: 50 random levels per view, distance from the price taken from the real pivot distances (in ADR), random side, same time window and rules. Mirror: one level at the same distance on the other side of the price.

## Horizon: until the next view or the New York close

| Set | Levels | Touched | Bounce | Bounce rate | 95% interval | Break | Pierced | No result |
|---|---|---|---|---|---|---|---|---|
| TC All pairs | 357 | 115 (32%) | 55 | **47.8%** | 39%-57% | 13 | 13 | 34 |
| TC pivot below price (support) | 182 | 59 (32%) | 27 | **45.8%** | 34%-58% | 8 | 6 | 18 |
| TC pivot above price (resistance) | 175 | 56 (32%) | 28 | **50.0%** | 37%-63% | 5 | 7 | 16 |
| TC EURUSD | 50 | 16 (32%) | 9 | **56.2%** | 33%-77% | 0 | 3 | 4 |
| TC GBPUSD | 41 | 11 (27%) | 6 | **54.5%** | 28%-79% | 0 | 0 | 5 |
| TC USDCHF | 48 | 18 (38%) | 12 | **66.7%** | 44%-84% | 1 | 2 | 3 |
| TC USDJPY | 57 | 27 (47%) | 7 | **25.9%** | 13%-45% | 5 | 2 | 13 |
| TC USDCAD | 39 | 4 (10%) | 0 | **0.0%** | 0%-49% | 2 | 0 | 2 |
| TC AUDUSD | 42 | 10 (24%) | 5 | **50.0%** | 24%-76% | 1 | 2 | 2 |
| TC NZDUSD | 41 | 19 (46%) | 11 | **57.9%** | 36%-77% | 3 | 1 | 4 |
| TC EURGBP | 39 | 10 (26%) | 5 | **50.0%** | 24%-76% | 1 | 3 | 1 |
| Random levels, all pairs | 17850 | 6280 (35%) | 3154 | **50.2%** | 49%-51% | 1210 | 940 | 976 |
| Mirror levels, all pairs | 357 | 122 (34%) | 64 | **52.5%** | 44%-61% | 27 | 13 | 18 |

Random levels by pair:

| Set | Levels | Touched | Bounce | Bounce rate | 95% interval | Break | Pierced | No result |
|---|---|---|---|---|---|---|---|---|
| EURUSD | 2500 | 921 (37%) | 478 | **51.9%** | 49%-55% | 148 | 148 | 147 |
| GBPUSD | 2050 | 745 (36%) | 384 | **51.5%** | 48%-55% | 115 | 132 | 114 |
| USDCHF | 2400 | 903 (38%) | 453 | **50.2%** | 47%-53% | 153 | 128 | 169 |
| USDJPY | 2850 | 987 (35%) | 437 | **44.3%** | 41%-47% | 269 | 127 | 154 |
| USDCAD | 1950 | 585 (30%) | 292 | **49.9%** | 46%-54% | 95 | 96 | 102 |
| AUDUSD | 2100 | 786 (37%) | 380 | **48.3%** | 45%-52% | 155 | 132 | 119 |
| NZDUSD | 2050 | 711 (35%) | 374 | **52.6%** | 49%-56% | 138 | 103 | 96 |
| EURGBP | 1950 | 642 (33%) | 356 | **55.5%** | 52%-59% | 137 | 74 | 75 |

## Horizon: until the New York close

| Set | Levels | Touched | Bounce | Bounce rate | 95% interval | Break | Pierced | No result |
|---|---|---|---|---|---|---|---|---|
| TC All pairs | 357 | 137 (38%) | 80 | **58.4%** | 50%-66% | 19 | 16 | 22 |
| TC pivot below price (support) | 182 | 72 (40%) | 41 | **56.9%** | 45%-68% | 11 | 8 | 12 |
| TC pivot above price (resistance) | 175 | 65 (37%) | 39 | **60.0%** | 48%-71% | 8 | 8 | 10 |
| TC EURUSD | 50 | 22 (44%) | 13 | **59.1%** | 39%-77% | 1 | 3 | 5 |
| TC GBPUSD | 41 | 16 (39%) | 13 | **81.2%** | 57%-93% | 0 | 0 | 3 |
| TC USDCHF | 48 | 23 (48%) | 17 | **73.9%** | 54%-87% | 1 | 4 | 1 |
| TC USDJPY | 57 | 28 (49%) | 9 | **32.1%** | 18%-51% | 5 | 6 | 8 |
| TC USDCAD | 39 | 6 (15%) | 2 | **33.3%** | 10%-70% | 3 | 0 | 1 |
| TC AUDUSD | 42 | 11 (26%) | 7 | **63.6%** | 35%-85% | 2 | 1 | 1 |
| TC NZDUSD | 41 | 21 (51%) | 14 | **66.7%** | 45%-83% | 3 | 1 | 3 |
| TC EURGBP | 39 | 10 (26%) | 5 | **50.0%** | 24%-76% | 4 | 1 | 0 |
| Random levels, all pairs | 17850 | 7223 (40%) | 4021 | **55.7%** | 55%-57% | 1573 | 904 | 725 |
| Mirror levels, all pairs | 357 | 153 (43%) | 87 | **56.9%** | 49%-64% | 38 | 14 | 14 |

Random levels by pair:

| Set | Levels | Touched | Bounce | Bounce rate | 95% interval | Break | Pierced | No result |
|---|---|---|---|---|---|---|---|---|
| EURUSD | 2500 | 1143 (46%) | 665 | **58.2%** | 55%-61% | 224 | 154 | 100 |
| GBPUSD | 2050 | 831 (41%) | 501 | **60.3%** | 57%-64% | 127 | 116 | 87 |
| USDCHF | 2400 | 1055 (44%) | 639 | **60.6%** | 58%-63% | 190 | 121 | 105 |
| USDJPY | 2850 | 1097 (38%) | 514 | **46.9%** | 44%-50% | 285 | 143 | 155 |
| USDCAD | 1950 | 704 (36%) | 370 | **52.6%** | 49%-56% | 138 | 116 | 80 |
| AUDUSD | 2100 | 881 (42%) | 471 | **53.5%** | 50%-57% | 206 | 112 | 92 |
| NZDUSD | 2050 | 795 (39%) | 449 | **56.5%** | 53%-60% | 204 | 77 | 65 |
| EURGBP | 1950 | 717 (37%) | 412 | **57.5%** | 54%-61% | 199 | 65 | 41 |
