# AleProfit

https://gentle-river-035083a03.6.azurestaticapps.net/?clientId=ee2b6b01-1a32-413a-bc80-895d92add18e - Adres aplikacji

*Aby zobaczyć aktualne dane po zmianach należy kliknąć "Sync Data" w prawym górnym rogu.*

AleProfit to system analityczny służący do precyzyjnego wyliczania rentowności sprzedaży w serwisie Allegro. Aplikacja agreguje dane o zamówieniach, kosztach logistycznych oraz prowizjach, dostarczając sprzedawcy informację o rzeczywistym zysku netto po uwzględnieniu podatków dochodowych i VAT.

## Architektura i przepływ danych

System opiera się na architekturze, w której Azure SQL Database stanowi centralny silnik logiki biznesowej.

1.  **Ingestia danych:** Azure Functions pobierają surowe dane w formacie JSON z Allegro REST API.
2.  **Przetwarzanie (Shredding):** Dane JSON są przekazywane bezpośrednio do procedur składowanych T-SQL, gdzie za pomocą operatora `OPENJSON` są parsowane i mapowane na tabele relacyjne.
3.  **Logika finansowa:** Zdecydowana większość kalkulacji marży, podatków i prowizji odbywa się po stronie bazy danych w widokach i procedurach, co gwarantuje spójność danych w bazie.

## Warstwa bazodanowa (T-SQL)

Projekt kładzie duży nacisk na integralność danych i wydajność operacji masowych.

### Kluczowe mechanizmy SQL:
*   **Parsowanie JSON:** Logika `sp_UpsertAllegroOrdersFromJSON` wyodrębnia dane o produktach bezpośrednio z obiektu zamówienia. Dzięki temu system zachowuje informacje o nazwie i cenie produktu z momentu transakcji, nawet jeśli pierwotna oferta w Allegro została usunięta lub zmieniona.
*   **Silnik kalkulacji P&L:** Widok `vw_OrderProfitability_Detailed` stanowi serce systemu. Łączy on dane z tabel zamówień, pozycji liniowych, billingów oraz kosztów zdefiniowanych przez użytkownika (COGS).

### Obsługa logiki finansowej w SQL:
*   **Obsługa VAT:** Dynamiczne wyliczanie wartości netto dla różnych stawek (23%, 8%, 5%, 0%) oraz obsługa "świadczeń złożonych" (przypisywanie stawki VAT wysyłki na podstawie zawartości koszyka).

## Schemat bazy danych

Główne obiekty w schemacie:
*   `Clients`: Zarządzanie tożsamością użytkowników.
*   `AllegroOrders` & `OrderLineItems`: Znormalizowane dane o transakcjach.
*   `AllegroBillingEntries`: Rejestr operacji finansowych na koncie Allegro.
*   `OfferMasterData`: Kartoteka produktów z definicją kosztów zakupu i opakowania.
*   `AllegroFeeCategories`: Słownik mapujący opłaty na kategorie księgowe.

## Tech Stack

*   **Baza danych:** Azure SQL Database.
*   **Język logiki:** T-SQL.
*   **Backend:** Azure Function App.
*   **Frontend:** HTML / JS / Bootstrap (Azure Static Web App).

## Wdrożenie

Skrypty SQL niezbędne do uruchomienia bazy znajdują się w folderze `/db`:
1.  `Schema_Creation.sql` – struktura tabel i indeksów.
2.  `sp_Upsert...` – procedury do synchronizacji danych z JSON.
3.  `vw_OrderProfitability_Detailed.sql` – główny widok analityczny.

Wymagane jest skonfigurowanie zmiennej środowiskowej `SqlConnectionString` oraz kluczy Allegro API w Azure Function App, aby umożliwić dostęp do bazy danych.

## Uwagi

W zakładce *Koszty produktów* możliwe jest wybranie VAT, kosztu pakowania, kosztu zakupu a następnie zapisanie. Po wciśnięciu *Sync Data* dane w pozostałych zakładkach zostaną zaktualizowane.

Aktualna wersja aplikacji obsługuje tylko zakupy wykonane w **polskiej wersji serwisu Allegro** w PLN.

Wyliczane zyski zakładają prostą wersję rozliczania podatków przez klienta, biorąc pod uwagę typowe wartości podatku. Ze względu na zmieniający się zakres danych zakładają tylko 3 przypadki:
 - skala podatkowa: 12% + 9% składka zdrowotna,
 - podatek liniowy: 19% + 4.9% składka zdrowotna,
 - ryczałt: 8.5% kalkulowane bez odliczeń kosztów i bez składki zdrowotnej.
