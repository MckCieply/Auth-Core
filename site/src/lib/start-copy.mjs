// The Start page copy of spec 0018, Appendix A, in English and Polish. The badge and the version lines are not here:
// they come from versions.json and the summary files (Settled 4).
export const QUICKSTART_URL = 'https://github.com/MckCieply/Auth-Core/blob/main/README.md#how-to-use-it';

export const STEP_YAML = [
  'permissions: [documents:read, documents:write, documents:approve]',
  'default_roles:',
  '  admin: ["*"]',
  '  user: [documents:read, documents:write]',
].join('\n');

export const START_COPY = {
  en: {
    latest: 'latest',
    headline: 'Sign-in for products used by many companies.',
    subline:
      'Auth-Core is a .NET service that runs next to your product, at /auth on your domain. It handles sign-in, password reset, ' +
      'invitations, companies and roles. Your backend checks its tokens offline, in any language.',
    fitButton: 'Check if it fits',
    quickstartButton: 'Quickstart on GitHub',
    factsLabel: 'Facts',
    facts: ['.NET 10', 'PostgreSQL', 'Docker, linux/amd64', 'RS256 JWT + JWKS', 'REST API, your own screens'],
    benefitsHeading: 'What you get',
    benefits: [
      {
        title: 'Login you build once.',
        text:
          'One container and a database give you sign-in, sessions, password reset, email verification, invitations, companies ' +
          'and roles. The same in every product.',
      },
      {
        title: 'Safe defaults.',
        text:
          'The refresh token sits in an HttpOnly cookie and rotates on every use. The access token lasts 10 minutes and lives in ' +
          'memory only. Lockout, rate limits and an audit log are on from the start.',
      },
      {
        title: 'Out of your API path.',
        text: 'Your backend checks each token against public keys it caches. Your API calls never pass through Auth-Core.',
      },
      {
        title: 'Your data, your server.',
        text:
          'Users stay in your PostgreSQL database and you pay no per-user fee. Sign-in with Google, Microsoft and GitHub is there ' +
          'when you turn it on.',
      },
    ],
    diagramHeading: 'How it sits next to your product',
    diagram: {
      frontend: { name: 'Your frontend', text: 'draws the login, reset and invitation screens' },
      proxy: { name: 'Your reverse proxy', text: 'HTTPS, one domain' },
      auth: { path: '/auth', name: 'Auth-Core', text: 'issues tokens, serves the keys, manages companies' },
      api: { path: '/api', name: 'your backend', text: 'checks the JWT offline' },
    },
    licenceHeading: 'License and price',
    licenceText:
      'Source-available under the Business Source License 1.1. Free for development, testing and evaluation, for personal ' +
      'non-commercial projects, and for non-commercial use by non-profit or educational organisations. A company running it in ' +
      'production, even internally, needs a commercial licence: EUR 490 a year per product, net of VAT, any number of users. Each ' +
      'version becomes Apache 2.0 three years after it is published.',
    licenceLink: 'Read the license in plain words',
    fitHeading: 'Is it for me?',
    goodFitHeading: 'A good fit if',
    goodFit: [
      'you build a product where companies, their members and roles matter',
      'your backend can check a JWT, in any language',
      'you can run a Linux server with Docker, a domain and an SMTP relay',
    ],
    elsewhereHeading: 'Look elsewhere if',
    elsewhere: [
      'you want ready-made login screens: your frontend draws them',
      'you need MFA today: it is planned, with no date',
      'several products must share one pool of users',
      'you need SAML, SCIM or LDAP',
    ],
    versionsHeading: 'Latest versions',
    versionsLink: 'All versions and the changelog',
    stepsHeading: 'Three steps to the first sign-in',
    steps: [
      {
        title: 'Declare permissions.',
        text: 'List the permissions your code checks and the roles a new company starts with, in one YAML file.',
      },
      {
        title: 'Run it on your server.',
        text:
          'A production compose file and a guide take you from a Linux server with Docker, a domain and an SMTP relay to a first ' +
          'sign-in.',
      },
      {
        title: 'Create the first company.',
        text: 'One command creates the company, a second invites its admin. The admin invites the rest.',
      },
    ],
  },
  pl: {
    latest: 'najnowsza',
    headline: 'Logowanie dla produktów, z których korzysta wiele firm.',
    subline:
      'Auth-Core to usługa w .NET, która działa obok Twojego produktu, pod /auth w Twojej domenie. Obsługuje logowanie, reset ' +
      'hasła, zaproszenia, firmy i role. Twój backend sprawdza jej tokeny offline, w dowolnym języku.',
    fitButton: 'Sprawdź, czy pasuje',
    quickstartButton: 'Szybki start na GitHubie',
    factsLabel: 'Fakty',
    facts: ['.NET 10', 'PostgreSQL', 'Docker, linux/amd64', 'RS256 JWT + JWKS', 'REST API, Twoje ekrany'],
    benefitsHeading: 'Co dostajesz',
    benefits: [
      {
        title: 'Logowanie budujesz raz.',
        text:
          'Jeden kontener i baza dają logowanie, sesje, reset hasła, weryfikację e-maila, zaproszenia, firmy i role. Tak samo w ' +
          'każdym produkcie.',
      },
      {
        title: 'Bezpieczne od startu.',
        text:
          'Refresh token siedzi w ciasteczku HttpOnly i zmienia się przy każdym użyciu. Access token żyje 10 minut, tylko w ' +
          'pamięci. Blokada konta, limity zapytań i log audytu działają od pierwszego dnia.',
      },
      {
        title: 'Poza ścieżką Twojego API.',
        text: 'Twój backend sprawdza każdy token kluczami publicznymi z cache. Wywołania Twojego API nie przechodzą przez Auth-Core.',
      },
      {
        title: 'Twoje dane, Twój serwer.',
        text:
          'Użytkownicy zostają w Twojej bazie PostgreSQL i nie płacisz za użytkownika. Logowanie przez Google, Microsoft i GitHub ' +
          'włączasz, kiedy chcesz.',
      },
    ],
    diagramHeading: 'Jak to stoi obok Twojego produktu',
    diagram: {
      frontend: { name: 'Twój frontend', text: 'rysuje ekrany logowania, resetu i zaproszeń' },
      proxy: { name: 'Twój reverse proxy', text: 'HTTPS, jedna domena' },
      auth: { path: '/auth', name: 'Auth-Core', text: 'wydaje tokeny, udostępnia klucze, zarządza firmami' },
      api: { path: '/api', name: 'Twój backend', text: 'sprawdza JWT offline' },
    },
    licenceHeading: 'Licencja i cena',
    licenceText:
      'Kod dostępny na licencji Business Source License 1.1. Za darmo do developmentu, testów i oceny, do projektów osobistych bez ' +
      'celów komercyjnych oraz do niekomercyjnego użytku organizacji non-profit i edukacyjnych. Firma, która uruchamia go ' +
      'produkcyjnie, nawet wewnętrznie, potrzebuje licencji komercyjnej: 490 EUR netto rocznie za produkt, dowolna liczba ' +
      'użytkowników. Każda wersja przechodzi na Apache 2.0 trzy lata po publikacji.',
    licenceLink: 'Licencja prostymi słowami',
    fitHeading: 'Czy to dla mnie?',
    goodFitHeading: 'Pasuje, jeśli',
    goodFit: [
      'budujesz produkt, w którym liczą się firmy, ich członkowie i role',
      'Twój backend umie sprawdzić JWT, w dowolnym języku',
      'możesz utrzymać serwer z Linuksem, Dockerem, domeną i SMTP',
    ],
    elsewhereHeading: 'Szukaj gdzie indziej, jeśli',
    elsewhere: [
      'chcesz gotowych ekranów logowania: rysuje je Twój frontend',
      'potrzebujesz MFA już dziś: jest w planach, bez daty',
      'kilka produktów ma dzielić jedną pulę użytkowników',
      'potrzebujesz SAML, SCIM albo LDAP',
    ],
    versionsHeading: 'Najnowsze wersje',
    versionsLink: 'Wszystkie wersje i changelog',
    stepsHeading: 'Trzy kroki do pierwszego logowania',
    steps: [
      {
        title: 'Zadeklaruj uprawnienia.',
        text: 'W jednym pliku YAML wypisz uprawnienia, które sprawdza Twój kod, i role, z którymi startuje nowa firma.',
      },
      {
        title: 'Uruchom na swoim serwerze.',
        text:
          'Gotowy plik compose i przewodnik prowadzą od serwera z Linuksem, Dockerem, domeną i SMTP do pierwszego logowania.',
      },
      {
        title: 'Załóż pierwszą firmę.',
        text: 'Jedno polecenie tworzy firmę, drugie zaprasza jej admina. Admin zaprasza resztę.',
      },
    ],
  },
};
