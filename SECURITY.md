# Security

Tangent Atlas handles people's financial information, so we take reports of security problems seriously and want to hear about them.

## Reporting a vulnerability

Please report it privately through GitHub: on this repository's **Security** tab, choose **Report a vulnerability**. Do not open a public issue.

Tell us what you found, how to reproduce it, and what you think the impact is. We will acknowledge your report within three business days, keep you informed while we fix it, and credit you when the fix is published if you would like.

## Scope

This repository's code and the services built from it. Please do not test against other people's accounts or data, degrade the service, or access more data than you need to show the problem.

## How we work

We follow a written security program based on the FTC Safeguards Rule, OWASP ASVS Level 2 and the Microsoft cloud security benchmark. Every change goes through Git and pull requests; infrastructure is built by script; no secrets are kept in source code (managed identities, federated sign-in, and certificates with non-exportable keys instead).
