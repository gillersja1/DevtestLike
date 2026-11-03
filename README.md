# DevtestLike

[![CI](https://github.com/gillersja1/DevtestLike/actions/workflows/ci.yml/badge.svg)](https://github.com/gillersja1/DevtestLike/actions/workflows/ci.yml)

The kind of test automation Broadcom **DevTest** (CA LISA, now Broadcom Service Virtualization) is used for, built with open tools in C#:
**authentication, XML-based orchestration and database validations across interconnected systems**, with the
unreliable third-party dependency replaced by a **virtual service**.

DevTest itself is licensed software. This project shows the same skills and uses the same concepts, mapped to
DevTest's names in the [glossary](#devtest-glossary) below.

## The systems under test

```
  Agency portal (the tests)
     │  OAuth2 access token (client credentials, from Keycloak)
     │  + WS-Security UsernameToken in the SOAP header
     ▼
  Policy service ── SOAP 1.1 / XML ──►  Rating engine  (third party: a virtual service, WireMock.Net)
     │
     ▼
  Postgres  (quotes, policies: the system of record)
```

- **Policy service** (`src/PolicyService`): a legacy-style SOAP/XML service with three operations: `GetQuote`, `IssuePolicy` and `GetPolicy`.
  - `GetQuote` calls the rating engine, applies the premium rules and saves the quote.
  - `IssuePolicy` binds a quote into a policy in one database transaction.
  - Errors come back as proper SOAP faults: `soap:Client` for the caller's mistakes, `soap:Server` for a dependency failing.
- **Rating engine**: someone else's SOAP service. It's **virtualized** with WireMock.Net: response rules matched on the request XML by XPath, plus outages, slowness, garbage and a stateful restart, on demand.
- **Keycloak**: the identity provider, a real one, started in Docker with a realm defined in `tests/.../devtestlike-realm.json`. It has two clients: one allowed to call the service, and one that isn't.
- **Postgres**: real, in Docker. The tests read it directly to check what was actually written.

Every system starts automatically for a test run (Testcontainers): there's nothing to install except Docker and the .NET 10 SDK.

## What the tests cover

| Suite | What it checks |
|---|---|
| `QuoteToPolicyWorkflowTests` | **XML orchestration**: quote → bind → read back, each step built from the previous reply by **XPath**, every reply validated against the **XSD contract** (`contracts/policy.xsd`), and the **database** checked after each step. Also: binding twice, declined and expired quotes, unknown ids, malformed XML. |
| `AuthenticationTests` | **Both auth layers**: no token (401), forged token (401), a real token for the wrong application (403), wrong client secret (Keycloak refuses), missing or wrong **WS-Security** UsernameToken (SOAP fault), and that a refused call writes nothing and never reaches the rating engine. |
| `VirtualServiceTests` | **Service virtualization**: what the policy service sends the rating engine (and what it doesn't, like the applicant's name); outage, timeout and garbage handled as `soap:Server` faults with nothing saved; a **stateful** virtual service that's down once, then back. |
| `DataDrivenQuoteTests` | A **data set** (`DataSets/applicants.csv`): one test per row, covering the age bands, the premium math and the decline rule, checked in the reply and the database. Adding a scenario means adding a row. |

## Run it

```bash
dotnet test
```

The first run downloads the Postgres and Keycloak images; after that a full run takes a few seconds plus container start-up.

## DevTest glossary

| DevTest | Here |
|---|---|
| Virtual service (VS), virtual service model (VSM) | The WireMock.Net rating engine (`Environment/RatingEngine.cs`) |
| Service image (VSI) and its transactions | The response rules in `WithStandardRules()`, matched on the request body by XPath |
| Stateful (conversational) transactions | `DownOnceThenUp()`, a WireMock scenario |
| Think time and response delays | `Slow(...)`, WireMock's `WithDelay` |
| Test case and its steps | A test method, such as `QuoteThenBindThenReadBack` (quote, bind, read back) |
| XML XPath filter (save a value into a property) | `reply.Value("//p:quoteId")`, which feeds the next step |
| Assertions (XML XPath, XML validation) | NUnit assertions on XPath values; `ConformsToContract()` validates against the XSD |
| JDBC step and result-set assertions | `Environment/Db.cs`, which queries Postgres directly |
| Data set (CSV) | `DataSets/applicants.csv` with `TestCaseSource` |
| Config and environment | `Environment/Enterprise.cs`, which starts and wires every system |
| Test suite run in CI | GitHub Actions (`.github/workflows/ci.yml`); results go to Grafana |

## CI

Every push and pull request runs the whole suite on GitHub Actions, which provides Docker for the Postgres and Keycloak containers. Results go to Grafana through [test-results-otel](https://github.com/gillersja1/test-results-otel).

## Next

Planned: a message queue step (JMS-style, ActiveMQ), SAML sign-in through Keycloak, and record-and-playback of a live service into a virtual one.
