# AleProfit - System Analityczny Rentowności Allegro

AleProfit to zaawansowany system analityczny zaprojektowany dla sprzedawców Allegro, którzy chcą precyzyjnie kontrolować swoją marżę. W przeciwieństwie do prostych kalkulatorów, AleProfit agreguje realne dane o zamówieniach, kosztach logistycznych i prowizjach bezpośrednio z API Allegro.

---

## Szybki start
* Adres aplikacji: [aleprofit.azurestaticapps.net](https://gentle-river-035083a03.6.azurestaticapps.net/?clientId=ee2b6b01-1a32-413a-bc80-895d92add18e)
* Synchronizacja: Aby odświeżyć dane po zmianach w konfiguracji kosztów, użyj przycisku "Sync Data" w prawym górnym rogu.

---

## Architektura i Przepływ Danych

System wykorzystuje architekturę Data-Driven, gdzie logika biznesowa jest osadzona blisko danych (Azure SQL Database).

1. Ingestia (Azure Functions): Bezstanowe funkcje pobierają dane JSON z Allegro REST API (Order & Billing API).
2. Przetwarzanie (T-SQL OPENJSON): Surowy JSON trafia do procedur składowanych, które w sposób atomowy parsują i aktualizują relacyjną bazę danych.
3. Silnik P&L (Widoki): Kalkulacje zysku netto, podatków i prowizji odbywają się w czasie rzeczywistym w widoku vw_OrderProfitability_Detailed.

### Kluczowe funkcjonalności SQL
* Snapshotting cen: sp_UpsertAllegroOrdersFromJSON utrwala cenę zakupu i nazwę produktu z momentu transakcji (odporność na edycję ofert).
* Inteligentny VAT: Automatyczne przypisywanie stawek VAT dla kosztów wysyłki na podstawie analizy koszyka zakupowego.
* Agregacja Billingów: Powiązanie wpisów z Allegro Finanse bezpośrednio z konkretnymi zamówieniami.

---

## Tech Stack

* Frontend: HTML5, Modern JS (ES6+), Bootstrap 5.
* Backend: Azure Functions (C# / .NET).
* Baza danych: Azure SQL Database (T-SQL).
* Integracje: Allegro REST API (OAuth 2.0).

---

## Struktura Projektu

* /backend – Kod źródłowy Azure Functions (obsługa Auth, Sync, API endpoints).
* /db – Skrypty bazy danych:
    * /Schema – Definicje tabel i indeksów.
    * /Stored_Procedures – Logika parsowania JSON i operacje CRUD.
    * /Functions & /Views – Silnik kalkulacyjny P&L.
* /frontend – Interfejs użytkownika (Single Page Application).

---

## Metodologia Wyliczeń

Aktualna wersja obsługuje rynek polski (PLN) i zakłada następujące scenariusze podatkowe:
* Skala podatkowa: 12% dochodowy + 9% składka zdrowotna.
* Podatek liniowy: 19% dochodowy + 4.9% składka zdrowotna.
* Ryczałt: 8.5% (liczone od przychodu, bez odliczeń kosztów).

Uwaga: Koszty zakupu towaru (COGS) oraz koszty opakowania definiuje się w zakładce Koszty produktów. System automatycznie uwzględni te parametry przy kolejnej synchronizacji.

---

## Uwagi i Ograniczenia
* System obsługuje wyłącznie transakcje w walucie PLN.
* Dane o kosztach logistycznych są pobierane z historii billingowej Allegro (wymaga pełnej synchronizacji).
