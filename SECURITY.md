# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in Performance Studio, please report it responsibly.

**Do not open a public GitHub issue for security vulnerabilities.**

Instead, please email **erik@erikdarling.com** with:

- A description of the vulnerability
- Steps to reproduce the issue
- The potential impact
- Any suggested fixes (optional)

You should receive a response within 72 hours. We will work with you to understand the issue and coordinate a fix before any public disclosure.

## Scope

This policy applies to:

- Desktop application (PlanViewer.App)
- Core analysis library (PlanViewer.Core)
- CLI tool (PlanViewer.Cli)
- SSMS extension (PlanViewer.Ssms)

## Security Best Practices

When using Performance Studio:

- Use Windows Authentication where possible when connecting to SQL Server
- Use dedicated accounts with minimal required permissions
- Enable encryption for SQL Server connections
- Keep your SQL Server instances patched and up to date
