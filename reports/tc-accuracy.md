# Trading Central accuracy check

Intraday views, 2026-08-16 .. 2026-10-01, pairs EURUSD, GBPUSD, USDCHF, USDJPY, USDCAD, AUDUSD, NZDUSD, EURGBP. One view = one distinct set of pivot and targets; repeats with the same levels are merged. Prices are FxPro bid minutes. Spread used for the pip result: 1 pip.

Distinct views: 358. Dropped before the check: no targets in text 28

## Horizon: until the next view or the New York close

Status: ok 357, price outside pivot..target 1

### All pairs

- views checked: 357, resolved (target or pivot reached): 136, open at the end: 221
- first target before pivot: 64 of 136 = 47.1%; random walk expects 42.0% (57.2); z = +1.22
- mirrored trade (same distances, other way): 43 of 141 = 30.5%
- second target before pivot: 17 of 80 = 21.2%
- trade P0 -> T1 / stop at pivot, avg pips per view: +1.5 (after 1 pip spread +0.5); mirrored -1.2; total +527 vs mirrored -412 pips
- TC direction equals the last 24 h move in 218 of 258 = 84%; 'follow the last 24 h' with the same distances: 49 of 113 = 43.4%, avg +1.3 pips
- median distance to T1 28.9 pips, to pivot 21.3 pips

### EURUSD

- views checked: 50, resolved (target or pivot reached): 24, open at the end: 26
- first target before pivot: 13 of 24 = 54.2%; random walk expects 39.8% (9.6); z = +1.49
- mirrored trade (same distances, other way): 5 of 21 = 23.8%
- second target before pivot: 4 of 13 = 30.8%
- trade P0 -> T1 / stop at pivot, avg pips per view: +2.5 (after 1 pip spread +1.5); mirrored -3.1; total +125 vs mirrored -153 pips
- TC direction equals the last 24 h move in 29 of 35 = 83%; 'follow the last 24 h' with the same distances: 10 of 18 = 55.6%, avg +4.2 pips
- median distance to T1 25.8 pips, to pivot 18.6 pips

### GBPUSD

- views checked: 41, resolved (target or pivot reached): 15, open at the end: 26
- first target before pivot: 9 of 15 = 60.0%; random walk expects 46.4% (7.0); z = +1.08
- mirrored trade (same distances, other way): 4 of 16 = 25.0%
- second target before pivot: 3 of 9 = 33.3%
- trade P0 -> T1 / stop at pivot, avg pips per view: +1.9 (after 1 pip spread +0.9); mirrored -0.9; total +80 vs mirrored -35 pips
- TC direction equals the last 24 h move in 28 of 30 = 93%; 'follow the last 24 h' with the same distances: 8 of 14 = 57.1%, avg +1.6 pips
- median distance to T1 37.6 pips, to pivot 26.1 pips

### USDCHF

- views checked: 48, resolved (target or pivot reached): 16, open at the end: 32
- first target before pivot: 6 of 16 = 37.5%; random walk expects 41.0% (6.6); z = -0.30
- mirrored trade (same distances, other way): 6 of 19 = 31.6%
- second target before pivot: 1 of 11 = 9.1%
- trade P0 -> T1 / stop at pivot, avg pips per view: +3.5 (after 1 pip spread +2.5); mirrored -3.2; total +168 vs mirrored -156 pips
- TC direction equals the last 24 h move in 27 of 34 = 79%; 'follow the last 24 h' with the same distances: 4 of 13 = 30.8%, avg +0.9 pips
- median distance to T1 31.0 pips, to pivot 19.3 pips

### USDJPY

- views checked: 58, resolved (target or pivot reached): 29, open at the end: 29
- first target before pivot: 11 of 29 = 37.9%; random walk expects 42.2% (12.3); z = -0.49
- mirrored trade (same distances, other way): 10 of 25 = 40.0%
- second target before pivot: 5 of 19 = 26.3%
- trade P0 -> T1 / stop at pivot, avg pips per view: -1.8 (after 1 pip spread -2.8); mirrored +2.7; total -103 vs mirrored +157 pips
- TC direction equals the last 24 h move in 35 of 43 = 81%; 'follow the last 24 h' with the same distances: 11 of 24 = 45.8%, avg +2.8 pips
- median distance to T1 58.1 pips, to pivot 45.4 pips

### USDCAD

- views checked: 39, resolved (target or pivot reached): 9, open at the end: 30
- first target before pivot: 6 of 9 = 66.7%; random walk expects 42.8% (3.9); z = +1.47
- mirrored trade (same distances, other way): 2 of 14 = 14.3%
- second target before pivot: 1 of 4 = 25.0%
- trade P0 -> T1 / stop at pivot, avg pips per view: +4.8 (after 1 pip spread +3.8); mirrored -5.6; total +187 vs mirrored -217 pips
- TC direction equals the last 24 h move in 23 of 27 = 85%; 'follow the last 24 h' with the same distances: 3 of 8 = 37.5%, avg +1.0 pips
- median distance to T1 34.0 pips, to pivot 23.7 pips

### AUDUSD

- views checked: 42, resolved (target or pivot reached): 14, open at the end: 28
- first target before pivot: 9 of 14 = 64.3%; random walk expects 43.2% (6.1); z = +1.64
- mirrored trade (same distances, other way): 8 of 19 = 42.1%
- second target before pivot: 2 of 7 = 28.6%
- trade P0 -> T1 / stop at pivot, avg pips per view: +2.4 (after 1 pip spread +1.4); mirrored -1.0; total +101 vs mirrored -41 pips
- TC direction equals the last 24 h move in 28 of 31 = 90%; 'follow the last 24 h' with the same distances: 4 of 11 = 36.4%, avg -1.6 pips
- median distance to T1 22.6 pips, to pivot 17.6 pips

### NZDUSD

- views checked: 41, resolved (target or pivot reached): 18, open at the end: 23
- first target before pivot: 7 of 18 = 38.9%; random walk expects 44.2% (8.0); z = -0.46
- mirrored trade (same distances, other way): 6 of 15 = 40.0%
- second target before pivot: 1 of 12 = 8.3%
- trade P0 -> T1 / stop at pivot, avg pips per view: -1.0 (after 1 pip spread -2.0); mirrored +1.7; total -41 vs mirrored +69 pips
- TC direction equals the last 24 h move in 24 of 31 = 77%; 'follow the last 24 h' with the same distances: 6 of 17 = 35.3%, avg -0.8 pips
- median distance to T1 23.3 pips, to pivot 17.9 pips

### EURGBP

- views checked: 38, resolved (target or pivot reached): 11, open at the end: 27
- first target before pivot: 3 of 11 = 27.3%; random walk expects 36.3% (4.0); z = -0.65
- mirrored trade (same distances, other way): 2 of 12 = 16.7%
- second target before pivot: 0 of 5 = 0.0%
- trade P0 -> T1 / stop at pivot, avg pips per view: +0.2 (after 1 pip spread -0.8); mirrored -0.9; total +9 vs mirrored -36 pips
- TC direction equals the last 24 h move in 24 of 27 = 89%; 'follow the last 24 h' with the same distances: 3 of 8 = 37.5%, avg +1.5 pips
- median distance to T1 12.7 pips, to pivot 8.3 pips

### Bullish views

- views checked: 183, resolved (target or pivot reached): 59, open at the end: 124
- first target before pivot: 19 of 59 = 32.2%; random walk expects 40.2% (23.7); z = -1.30
- mirrored trade (same distances, other way): 20 of 63 = 31.7%
- second target before pivot: 2 of 32 = 6.2%
- trade P0 -> T1 / stop at pivot, avg pips per view: -0.4 (after 1 pip spread -1.4); mirrored -0.0; total -68 vs mirrored -4 pips
- TC direction equals the last 24 h move in 104 of 124 = 84%; 'follow the last 24 h' with the same distances: 15 of 49 = 30.6%, avg -0.8 pips
- median distance to T1 29.5 pips, to pivot 20.1 pips

### Bearish views

- views checked: 174, resolved (target or pivot reached): 77, open at the end: 97
- first target before pivot: 45 of 77 = 58.4%; random walk expects 43.4% (33.5); z = +2.74
- mirrored trade (same distances, other way): 23 of 78 = 29.5%
- second target before pivot: 15 of 48 = 31.2%
- trade P0 -> T1 / stop at pivot, avg pips per view: +3.4 (after 1 pip spread +2.4); mirrored -2.3; total +595 vs mirrored -408 pips
- TC direction equals the last 24 h move in 114 of 134 = 85%; 'follow the last 24 h' with the same distances: 34 of 64 = 53.1%, avg +3.3 pips
- median distance to T1 27.9 pips, to pivot 22.0 pips

## Horizon: until the New York close, ignoring newer views

Status: ok 357, price outside pivot..target 1

### All pairs

- views checked: 357, resolved (target or pivot reached): 157, open at the end: 200
- first target before pivot: 69 of 157 = 43.9%; random walk expects 41.6% (65.3); z = +0.62
- mirrored trade (same distances, other way): 53 of 177 = 29.9%
- second target before pivot: 33 of 107 = 30.8%
- trade P0 -> T1 / stop at pivot, avg pips per view: +1.3 (after 1 pip spread +0.3); mirrored -2.2; total +465 vs mirrored -780 pips
- TC direction equals the last 24 h move in 218 of 258 = 84%; 'follow the last 24 h' with the same distances: 52 of 120 = 43.3%, avg +1.1 pips
- median distance to T1 28.9 pips, to pivot 21.3 pips

### EURUSD

- views checked: 50, resolved (target or pivot reached): 30, open at the end: 20
- first target before pivot: 14 of 30 = 46.7%; random walk expects 40.2% (12.1); z = +0.75
- mirrored trade (same distances, other way): 7 of 28 = 25.0%
- second target before pivot: 7 of 21 = 33.3%
- trade P0 -> T1 / stop at pivot, avg pips per view: +1.2 (after 1 pip spread +0.2); mirrored -4.7; total +62 vs mirrored -233 pips
- TC direction equals the last 24 h move in 29 of 35 = 83%; 'follow the last 24 h' with the same distances: 10 of 20 = 50.0%, avg +3.2 pips
- median distance to T1 25.8 pips, to pivot 18.6 pips

### GBPUSD

- views checked: 41, resolved (target or pivot reached): 18, open at the end: 23
- first target before pivot: 9 of 18 = 50.0%; random walk expects 44.5% (8.0); z = +0.48
- mirrored trade (same distances, other way): 4 of 17 = 23.5%
- second target before pivot: 5 of 12 = 41.7%
- trade P0 -> T1 / stop at pivot, avg pips per view: +1.3 (after 1 pip spread +0.3); mirrored -2.0; total +54 vs mirrored -83 pips
- TC direction equals the last 24 h move in 28 of 30 = 93%; 'follow the last 24 h' with the same distances: 8 of 14 = 57.1%, avg +1.6 pips
- median distance to T1 37.6 pips, to pivot 26.1 pips

### USDCHF

- views checked: 48, resolved (target or pivot reached): 20, open at the end: 28
- first target before pivot: 7 of 20 = 35.0%; random walk expects 40.3% (8.1); z = -0.50
- mirrored trade (same distances, other way): 6 of 26 = 23.1%
- second target before pivot: 2 of 12 = 16.7%
- trade P0 -> T1 / stop at pivot, avg pips per view: +2.1 (after 1 pip spread +1.1); mirrored -4.2; total +98 vs mirrored -203 pips
- TC direction equals the last 24 h move in 27 of 34 = 79%; 'follow the last 24 h' with the same distances: 5 of 15 = 33.3%, avg -0.2 pips
- median distance to T1 31.0 pips, to pivot 19.3 pips

### USDJPY

- views checked: 58, resolved (target or pivot reached): 30, open at the end: 28
- first target before pivot: 11 of 30 = 36.7%; random walk expects 42.3% (12.7); z = -0.64
- mirrored trade (same distances, other way): 12 of 31 = 38.7%
- second target before pivot: 7 of 22 = 31.8%
- trade P0 -> T1 / stop at pivot, avg pips per view: -0.1 (after 1 pip spread -1.1); mirrored +0.1; total -6 vs mirrored +3 pips
- TC direction equals the last 24 h move in 35 of 43 = 81%; 'follow the last 24 h' with the same distances: 11 of 24 = 45.8%, avg +2.8 pips
- median distance to T1 58.1 pips, to pivot 45.4 pips

### USDCAD

- views checked: 39, resolved (target or pivot reached): 11, open at the end: 28
- first target before pivot: 7 of 11 = 63.6%; random walk expects 42.1% (4.6); z = +1.47
- mirrored trade (same distances, other way): 3 of 18 = 16.7%
- second target before pivot: 2 of 6 = 33.3%
- trade P0 -> T1 / stop at pivot, avg pips per view: +7.0 (after 1 pip spread +6.0); mirrored -6.8; total +272 vs mirrored -264 pips
- TC direction equals the last 24 h move in 23 of 27 = 85%; 'follow the last 24 h' with the same distances: 3 of 8 = 37.5%, avg +1.3 pips
- median distance to T1 34.0 pips, to pivot 23.7 pips

### AUDUSD

- views checked: 42, resolved (target or pivot reached): 15, open at the end: 27
- first target before pivot: 9 of 15 = 60.0%; random walk expects 42.2% (6.3); z = +1.44
- mirrored trade (same distances, other way): 9 of 22 = 40.9%
- second target before pivot: 5 of 11 = 45.5%
- trade P0 -> T1 / stop at pivot, avg pips per view: +1.2 (after 1 pip spread +0.2); mirrored -0.4; total +50 vs mirrored -15 pips
- TC direction equals the last 24 h move in 28 of 31 = 90%; 'follow the last 24 h' with the same distances: 4 of 12 = 33.3%, avg -2.1 pips
- median distance to T1 22.6 pips, to pivot 17.6 pips

### NZDUSD

- views checked: 41, resolved (target or pivot reached): 20, open at the end: 21
- first target before pivot: 8 of 20 = 40.0%; random walk expects 43.7% (8.7); z = -0.34
- mirrored trade (same distances, other way): 7 of 19 = 36.8%
- second target before pivot: 3 of 15 = 20.0%
- trade P0 -> T1 / stop at pivot, avg pips per view: -1.8 (after 1 pip spread -2.8); mirrored +0.9; total -72 vs mirrored +36 pips
- TC direction equals the last 24 h move in 24 of 31 = 77%; 'follow the last 24 h' with the same distances: 6 of 17 = 35.3%, avg -0.8 pips
- median distance to T1 23.3 pips, to pivot 17.9 pips

### EURGBP

- views checked: 38, resolved (target or pivot reached): 13, open at the end: 25
- first target before pivot: 4 of 13 = 30.8%; random walk expects 36.7% (4.8); z = -0.46
- mirrored trade (same distances, other way): 5 of 16 = 31.2%
- second target before pivot: 2 of 8 = 25.0%
- trade P0 -> T1 / stop at pivot, avg pips per view: +0.1 (after 1 pip spread -0.9); mirrored -0.6; total +5 vs mirrored -21 pips
- TC direction equals the last 24 h move in 24 of 27 = 89%; 'follow the last 24 h' with the same distances: 5 of 10 = 50.0%, avg +1.8 pips
- median distance to T1 12.7 pips, to pivot 8.3 pips

### Bullish views

- views checked: 183, resolved (target or pivot reached): 71, open at the end: 112
- first target before pivot: 23 of 71 = 32.4%; random walk expects 39.8% (28.3); z = -1.32
- mirrored trade (same distances, other way): 23 of 85 = 27.1%
- second target before pivot: 8 of 42 = 19.0%
- trade P0 -> T1 / stop at pivot, avg pips per view: -0.3 (after 1 pip spread -1.3); mirrored -1.4; total -60 vs mirrored -253 pips
- TC direction equals the last 24 h move in 104 of 124 = 84%; 'follow the last 24 h' with the same distances: 16 of 53 = 30.2%, avg -1.2 pips
- median distance to T1 29.5 pips, to pivot 20.1 pips

### Bearish views

- views checked: 174, resolved (target or pivot reached): 86, open at the end: 88
- first target before pivot: 46 of 86 = 53.5%; random walk expects 43.0% (37.0); z = +2.02
- mirrored trade (same distances, other way): 30 of 92 = 32.6%
- second target before pivot: 25 of 65 = 38.5%
- trade P0 -> T1 / stop at pivot, avg pips per view: +3.0 (after 1 pip spread +2.0); mirrored -3.0; total +525 vs mirrored -528 pips
- TC direction equals the last 24 h move in 114 of 134 = 85%; 'follow the last 24 h' with the same distances: 36 of 67 = 53.7%, avg +3.2 pips
- median distance to T1 27.9 pips, to pivot 22.0 pips
