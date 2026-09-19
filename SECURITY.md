# Security policy

Framewright is a local artist workstation application. It is not designed to
be exposed directly to the public internet or operated as a multi-tenant
service.

## Supported configuration

- Run the Docker service on its default loopback binding.
- Use the paired HTTPS tablet flow for access from another device.
- Keep provider credentials in their supported local credential stores.
- Keep `App_Data`, generated media, `.env`, and Codex authentication files out
  of source control and workstation backups that are not encrypted.
- Treat imported projects, prompts, reference text, and media as untrusted
  input. They must never grant shell, provider, or arbitrary filesystem access.

Framewright intentionally does not accept arbitrary ComfyUI graphs from the
browser. Advanced workflow changes belong in the reviewed local workflow
library and must pass package smoke verification before use.

## Reporting a vulnerability

Do not open a public issue containing credentials, project media, or an exploit
that can expose local files. Send a private GitHub security advisory to the
repository owner with:

1. the affected version or commit;
2. the exact local/Docker configuration;
3. reproducible steps;
4. expected and observed impact; and
5. any safe mitigation already tested.

The project owner will acknowledge a complete report within seven days. A fix
is considered complete only after a regression test and a release note exist.
