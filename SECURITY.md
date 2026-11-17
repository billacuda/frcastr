# Security Policy

## Supported Versions

Only the latest version gets security updates. If you're on an older version, please upgrade before reporting a problem, in case it's already fixed.

| Version                                  | Supported          |
| ---------------------------------------- | ------------------ |
| [Latest version](CHANGELOG.md)           | :white_check_mark: |
| Older versions                           | :x:                |

## Reporting a Vulnerability

Please don't report security problems in a public issue, discussion, or pull request.

Report them privately through [GitHub's vulnerability reporting](https://github.com/billacuda/frcastr/security/advisories/new). Please include:

- The frcastr version, and which part is affected (web app, MQTT ingest, or the ESP32 sensor firmware)
- What an attacker could do, and what access they'd need
- Steps to reproduce it, with a proof of concept if you have one

Please leave out API keys, MQTT passwords, your station's exact location, and other private data.

## What to Expect

frcastr is maintained by one person, so these are goals, not guarantees:

- **Acknowledgement:** within 7 days of your report.
- **Updates:** at least every 14 days until it's resolved.
- **If it's accepted:** I'll work on a fix and release it in a new version. The changelog will describe it, and you'll be credited unless you'd rather not be.
- **If it's declined:** I'll explain why. For example, it might not be a security problem, or it might be in a third-party product.

Please give me a reasonable chance to release a fix before sharing details publicly.

## Scope

This policy covers the code in this repository. Please report problems in weather providers, MQTT brokers, or third-party software such as SQL Server, IIS, or the Arduino libraries to their makers. If frcastr uses one of these in a way that makes the problem worse, that's in scope.
