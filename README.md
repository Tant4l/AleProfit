# AleProfit - System Analityczny Rentowności Allegro

AleProfit to system obliczania rentowności zamówień dla sprzedawców w serwisie Allegro. Bierze od pod uwagę koszty zakupu produktów, pakowania, wysyłki, prowizji oraz podatków.

---

## Demo
* Adres aplikacji: https://gentle-river-035083a03.6.azurestaticapps.net/?clientId=ee2b6b01-1a32-413a-bc80-895d92add18e
* Synchronizacja: Aby odświeżyć dane po zmianach w konfiguracji kosztów, użyj przycisku **"Sync Data"** w prawym górnym rogu.
* Adres sprzedawcy w Allegro Sandbox: https://allegro.pl.allegrosandbox.pl/uzytkownik/AleDrogo

---

## Architektura i Przepływ Danych

System wykorzystuje 

1. Ingestia (Azure Functions): Funkcje pobierają dane JSON z Allegro REST API.
2. Przetwarzanie (T-SQL OPENJSON): Surowy JSON trafia do procedur składowanych, które parsują i aktualizują relacyjną bazę danych.
3. Silnik obliczeń (Widoki): Kalkulacje zysku netto, podatków i prowizji odbywają się w widoku vw_OrderProfitability_Detailed.

### Kluczowe funkcjonalności SQL
* Snapshotting cen: sp_UpsertAllegroOrdersFromJSON utrwala cenę zakupu i nazwę produktu z momentu transakcji.
* Inteligentny VAT: Automatyczne przypisywanie stawek VAT na podstawie zawartości koszyka zakupowego.
* Agregacja prowizji: Powiązanie kosztów prowizji bezpośrednio z konkretnymi zamówieniami.

---

## Tech Stack

* Frontend: *Azure Static Web App* (HTML5, JavaScript, Bootstrap 5).
* Backend: *Azure Function App* (C# / .NET).
* Baza danych: *Azure SQL Database* (T-SQL).
* Integracje: *Allegro REST API*.

---

## Struktura Projektu

* /backend – Kod źródłowy Azure Functions (obsługa Auth, Sync, endpointy API).
* /db – Skrypty bazy danych:
    * /Schema – Definicje tabel i indeksów.
    * /Stored_Procedures – Logika parsowania JSON i operacje.
    * /Functions & /Views – Silnik kalkulacji.
* /frontend – Interfejs użytkownika.

---

## Metodologia Wyliczeń

Aktualna wersja obsługuje rynek polski (PLN) i zakłada następujące scenariusze podatkowe:
* Skala podatkowa: 12% dochodowy + 9% składka zdrowotna.
* Podatek liniowy: 19% dochodowy + 4.9% składka zdrowotna.
* Ryczałt: 8.5% (liczone od przychodu, bez odliczeń kosztów).

Uwaga: Koszty zakupu towaru (COGS) oraz koszty opakowania definiuje się w zakładce **"Koszty produktów"**. System automatycznie uwzględni te parametry przy kolejnej synchronizacji.

---

## Uwagi i Ograniczenia
* System obsługuje wyłącznie transakcje w walucie PLN.
* Dane o kosztach są pobierane z historii billingowej Allegro (wymaga pełnej synchronizacji).
