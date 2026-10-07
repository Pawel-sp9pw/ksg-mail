# KSG Mail

Desktopowa aplikacja Windows do miesięcznej wysyłki dokumentów klientom przez SMTP.
MVP: .NET 8 + WPF, MailKit/MimeKit, SQLite, ZIP AES-256 (SharpZipLib), Windows DPAPI.

## Instalacja

Windows 10 (1809+) / Windows 11, x64. Pobierz `KsgMail-Setup-0.1.0-win-x64.exe`
z [Releases](https://github.com/Pawel-sp9pw/ksg-mail/releases), uruchom instalator i otwórz KSG Mail.
Instalacja dla bieżącego użytkownika, bez administratora. Własny runtime .NET jest dołączony.
Wariant przenośny: rozpakuj `KsgMail-0.1.0-win-x64.zip`, uruchom `KsgMail.exe`.
Nie przenoś katalogu programu po włączeniu harmonogramu; po przeniesieniu zapisz konfigurację ponownie.
Każdy build main udostępnia też instalator w zakładce Actions → KsgMail-win-x64.

## Pierwsza konfiguracja

1. Zakładka **SMTP i harmonogram**: host, port, STARTTLS (najczęściej 587) albo SSL/TLS (najczęściej 465),
   login, hasło, e-mail i nazwa nadawcy. Sprawdź parametry u dostawcy poczty.
   Niektóre konta wymagają hasła aplikacji oraz włączenia SMTP AUTH. MVP nie obsługuje OAuth2.
2. Ustaw limit ZIP (domyślnie 20 MB), limit źródłowych dokumentów (500 MB), liczbę prób (3).
   MIME/Base64 zwiększa rozmiar wiadomości o około 37%; ustaw limit ZIP poniżej limitu serwera.
   Program dodatkowo sprawdza limit SIZE ogłaszany przez SMTP.
3. **Zapisz konfigurację** i użyj **Test połączenia**. Test sprawdza TLS i logowanie, bez wysyłki.
4. Zakładka **Klienci** → **Nowy klient**: nazwa, jeden e-mail, temat, treść, katalog, hasło ZIP,
   aktywny/nieaktywny → **Zapisz klienta**. Każdy klient ma własną treść i własne hasło.
   Przy edycji puste pole hasła zachowuje poprzednie hasło; wpisanie nowego je zmienia.
5. W temacie i treści można użyć `{klient}` oraz `{miesiac}`, np. `Dokumenty za {miesiac}`.
   `{miesiac}` oznacza wybrany okres `RRRR-MM` (domyślnie bieżący miesiąc).
   Dokumenty nie są filtrowane po dacie — do archiwum trafiają wszystkie pliki z wybranego katalogu.

## Ręczna wysyłka i podgląd

Wybierz okres w **Wysyłka i podgląd**, następnie podgląd wszystkich aktywnych lub klienta zaznaczonego
w zakładce Klienci. Podgląd już tworzy zaszyfrowane archiwa: pokazuje odbiorcę, temat, treść,
nazwy plików, rzeczywisty rozmiar ZIP i błędy. Późniejsze zmiany plików źródłowych nie zmieniają podglądu.
Przycisk **Wyślij przygotowane wiadomości** wymaga potwierdzenia liczby odbiorców.

Błędni klienci nie blokują poprawnych: brak katalogu, pusty katalog, brak dostępu, zmienione pliki,
za duży ZIP lub źródła są zgłaszane osobno. Klienci nieaktywni nie są wysyłani.
ZIP obejmuje podkatalogi i zachowuje względne ścieżki. Limit: 10 000 plików.
Dowiązania/junctions i pliki z atrybutem reparse point są odrzucane — używaj lokalnych katalogów.
Katalog dokumentów nie powinien obejmować katalogu danych aplikacji.

Archiwa AES-256 można otworzyć np. w 7-Zip lub WinRAR. Wbudowany Eksplorator Windows może
nie obsługiwać tego szyfrowania. Hasło przekazuj klientowi osobnym kanałem. Nazwy plików w ZIP
nie są szyfrowane (ograniczenie formatu ZIP AES), zawartość jest szyfrowana.

## Miesięczny harmonogram

Włącz automatyczną wysyłkę, wybierz dzień 1–28 oraz godzinę lokalną Windows i zapisz konfigurację.
Pierwsze włączenie upoważnia aplikację do wysyłki do wszystkich aktywnych klientów bez
osobnego potwierdzenia każdego miesiąca.

Program rejestruje zadanie Windows `KSG-Mail-<SID użytkownika>`, wywołujące `KsgMail.exe --scheduled`
codziennie oraz przy logowaniu. Sama aplikacja sprawdza termin miesięczny i historię.
Udana wysyłka do klienta w danym okresie nie jest ponawiana. Gdy aplikacja jest otwarta,
jej timer sprawdza termin co minutę (po nieudanej próbie ponownie najwcześniej po godzinie).
Wysyłka automatyczna czeka na zakończenie operacji ręcznej / zamknięcie przygotowanego podglądu.

Komputer musi być włączony, a właściciel konfiguracji zalogowany. Zadanie nie przechowuje hasła
Windows. Po nieobecności nadrabia bieżący miesiąc; nie wysyła automatycznie zaległych wcześniejszych
miesięcy. Błędy można poprawić i ponowić ręcznie; następne uruchomienie zadania też próbuje klientów
bez udanej / niepewnej wysyłki. Wyłączenie harmonogramu i zapis usuwa zadanie.
Przy odinstalowaniu instalator próbuje usunąć zadanie; dane i historia pozostają.

## Historia, retry i ochrona przed duplikatami

Historia pokazuje ostatnie 1000 zdarzeń (pełna historia pozostaje w SQLite):
- **Wysłano** — SMTP przyjął wiadomość; nie gwarantuje dostarczenia do skrzynki klienta.
- **Błąd** — przygotowanie lub SMTP nie powiodły się.
- **Niepewny** — zerwane połączenie podczas wysyłki albo przerwanie procesu.
  Sprawdź u odbiorcy lub administratora poczty przed ponowieniem.
- **Pominięty** — blokada duplikatu.
- **W toku** — rezerwacja wysyłki.

Retry z rosnącym opóźnieniem obejmuje błędy połączenia przed wysyłką i jawne tymczasowe odpowiedzi
SMTP 4xx. Trwałe błędy logowania, TLS i SMTP 5xx nie są ponawiane w ramach jednej operacji.
Po zerwaniu połączenia podczas wysyłki nie ma automatycznej próby, ponieważ serwer mógł przyjąć mail.
Przerwane rezerwacje po restarcie otrzymują status Niepewny. Sukces i status Niepewny blokują
kolejną wysyłkę klienta w tym samym okresie.

Aby świadomie ponowić, zaznacz opcję **Zezwól na ponowną wysyłkę** PRZED przygotowaniem podglądu
i zatwierdź ostrzeżenie. Rezerwacji aktualnie W toku nie można wymusić.
Anulowanie zatrzymuje kolejne wiadomości; bieżąca wysyłka może zakończyć się jako Niepewny.
Aplikacja blokuje równoległe instancje dla tego samego konta plikiem blokady.

## Dane i bezpieczeństwo

Dane lokalne: `%LOCALAPPDATA%\KsgMail\ksg-mail.db`. Archiwa robocze: `%LOCALAPPDATA%\KsgMail\Temp`.
Baza SQLite zawiera klientów, ustawienia, rezerwacje i historię. Nazwy, adresy, treści i ścieżki
nie są szyfrowane. Hasła SMTP i ZIP są zapisywane wyłącznie jako szyfrogram DPAPI CurrentUser,
powiązany z kontem Windows; pola hasła w interfejsie są maskowane i nie odczytują zapisanych haseł.
Log nie zapisuje haseł, treści dokumentów ani surowych odpowiedzi SMTP.
Certyfikaty TLS są standardowo weryfikowane; nie ma opcji wyłączenia kontroli certyfikatu.
Nie jest obsługiwane nieszyfrowane SMTP.

ZIP-y powstają bez tworzenia niezaszyfrowanych kopii plików źródłowych. Po zakończeniu /
wyczyszczeniu podglądu są usuwane. Po awarii pozostałości są usuwane przy następnym starcie.
Zaszyfrowany ZIP nadal wymaga ochrony katalogu i dobrego hasła.
Wykonuj kopię całego katalogu danych po zamknięciu aplikacji. Przy migracji na inne konto / komputer
zapisz hasła ponownie; przeniesienie samej bazy nie zapewnia przenośności DPAPI.
Zmiana konta Windows nie przenosi konfiguracji automatycznie.
Odinstalowanie zachowuje bazę — usuń katalog danych ręcznie, jeśli chcesz skasować historię.

## Budowanie i testy

Wymagania deweloperskie: Windows x64, .NET SDK 8.0.425 (lub nowszy patch 8.0.4xx), Inno Setup 6.

```powershell
dotnet restore KsgMail.sln
dotnet test KsgMail.sln -c Release
dotnet run --project src/KsgMail.App
.\build.ps1 -Version 0.1.0
```

Wyniki: `artifacts/publish`, `artifacts/installer`, przenośny ZIP i `SHA256SUMS.txt`.
Instalator nie jest podpisany certyfikatem wydawcy; Windows może wyświetlić SmartScreen.
Repozytorium nie zawiera rzeczywistych klientów ani poświadczeń.

Testy obejmują DPAPI i zapisy SQLite, odczyt ZIP AES-256 i odrzucenie złego hasła,
brak / puste / zmienione / za duże pliki, walidację adresów, rezerwacje i odtwarzanie po awarii,
snapshot podglądu, wykluczenie nieaktywnych, harmonogram i załadowanie interfejsu WPF.
Testy SMTP używają lokalnego serwera TLS: sprawdzają zaszyfrowany załącznik MIME, retry 4xx,
brak retry 5xx, status Niepewny po zerwaniu połączenia oraz odrzucenie niezaufanego certyfikatu.
Nie wysyłają wiadomości do rzeczywistych odbiorców.

GitHub Actions: każdy push main / PR buduje, testuje i tworzy instalator.
Tag `v0.1.0` (format `vX.Y.Z`) dodatkowo tworzy GitHub Release z instalatorem i ZIP-em.

## Struktura

- `src/KsgMail.App` — interfejs WPF, uruchamianie zwykłe / harmonogram.
- `src/KsgMail.Core` — modele, walidacja, SQLite, DPAPI, archiwa, SMTP, wysyłka i zadanie Windows.
- `tests/KsgMail.Tests` — testy Windows.
- `installer` — Inno Setup.
- `.github/workflows/release.yml` — build, test, instalator, release.

Dokumentacja bibliotek:
[MailKit](https://mimekit.net/docs/html/Introduction.htm),
[SharpZipLib](https://icsharpcode.github.io/SharpZipLib/),
[DPAPI](https://learn.microsoft.com/dotnet/api/system.security.cryptography.protecteddata),
[SQLite](https://learn.microsoft.com/dotnet/standard/data/sqlite/),
[Inno Setup](https://jrsoftware.org/isinfo.php).
