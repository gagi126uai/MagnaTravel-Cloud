## Agent routing

Use specialized agents based on risk and scope.

### Backend work

Use `backend-dotnet-senior` for:

- APIs.
- Services.
- Domain logic.
- Persistence.
- Migrations.
- Integrations.
- Authentication.
- Authorization.
- Backend bugs.
- Backend performance.
- Backend tests.

After backend implementation, use `backend-dotnet-reviewer`.

Use `security-data-risk-reviewer` as an additional reviewer when the change touches:

- authentication;
- authorization;
- users;
- roles;
- agencies;
- passengers;
- bookings;
- payments;
- cancellations;
- refunds;
- invoices;
- vouchers;
- providers;
- integrations;
- migrations;
- audit logs;
- sensitive data.

### Frontend work

Use `frontend-senior` for:

- pages;
- components;
- forms;
- routing;
- state management;
- API integration;
- UX states;
- accessibility;
- frontend bugs;
- frontend tests.

After frontend implementation, use `frontend-reviewer`.

Use `qa-automation-senior` when the change affects important user flows or requires automated regression coverage.

### QA work

Use `qa-automation-senior` to create or update automated tests.

Use `qa-automation-reviewer` after tests are added or changed.

### Architecture work

Use `software-architect` for:

- non-trivial design decisions;
- module boundaries;
- integrations;
- migrations;
- refactors;
- scalability;
- security-sensitive design;
- provider/payment workflows.

Use `software-architect-reviewer` to challenge the proposal before implementation.

### Travel agency domain work

Use `travel-agency-domain-expert` whenever the task affects:

- bookings;
- quotes;
- reservations;
- confirmations;
- passengers;
- providers;
- payments;
- cancellations;
- refunds;
- vouchers;
- invoices;
- commissions;
- manual agency operations.

### Full workflow for high-risk business features

For changes involving bookings, payments, cancellations, refunds, vouchers, invoices, providers, or commissions:

1. `travel-agency-domain-expert`
2. `software-architect`
3. `software-architect-reviewer`
4. `backend-dotnet-senior`
5. `frontend-senior`, if UI is affected
6. `backend-dotnet-reviewer`
7. `frontend-reviewer`, if UI is affected
8. `security-data-risk-reviewer`
9. `qa-automation-senior`
10. `qa-automation-reviewer`

Do not use all agents for every small task. Activate agents according to risk.

## Tax and accounting agents

Use `arca-tax-expert-argentina` for Argentine tax-sensitive changes involving:

- ARCA/AFIP;
- electronic invoicing;
- invoice type;
- IVA;
- Ganancias;
- monotributo;
- régimen general;
- tax condition;
- credit notes;
- debit notes;
- refunds;
- cancellations;
- provider/customer tax treatment;
- withholdings;
- perceptions;
- fiscal reporting;
- foreign currency with tax impact.

Use `accounting-expert-argentina` for accounting-sensitive changes involving:

- accounting events;
- journal entries;
- chart of accounts;
- revenue recognition;
- receivables;
- payables;
- provider settlements;
- commissions;
- markups;
- refunds;
- credit/debit notes;
- currency differences;
- reconciliations;
- audit trails;
- accounting exports;
- financial reporting.

### Mandatory collaboration

Use both `arca-tax-expert-argentina` and `accounting-expert-argentina` together when a feature affects:

- invoices;
- credit notes;
- debit notes;
- refunds;
- cancellations;
- payments;
- provider settlements;
- commissions;
- markups;
- tax liabilities;
- accounting exports;
- ARCA integrations;
- financial reports.

### Tax/accounting truthfulness rules

These agents must not invent:

- tax rates;
- invoice types;
- ARCA requirements;
- accounting standards;
- accounting policies;
- chart of accounts;
- withholding/perception regimes;
- provincial or municipal obligations.

They must clearly label:

- verified facts;
- assumptions;
- tax risks;
- accounting risks;
- professional confirmation needed;
- unverified current legal validity.

### Professional validation rule

For production tax/accounting behavior, final validation must come from a qualified accountant or tax advisor.

Agents may help analyze, design, test, and review, but must not present themselves as final legal/tax/accounting authority.

## Infrastructure agent

Use `infrastructure-devops-docker` for changes or debugging involving:

- Docker;
- Docker Compose;
- Dockerfiles;
- MinIO;
- object storage;
- container networking;
- container volumes;
- environment variables;
- healthchecks;
- service startup order;
- local development setup;
- service logs;
- infrastructure documentation;
- database/cache/queue containers;
- multi-container orchestration.

### Mandatory infrastructure review

Use `infrastructure-devops-docker` before or during tasks that affect:

- `docker-compose.yml`;
- `compose.yml`;
- `Dockerfile`;
- `.dockerignore`;
- `.env.example`;
- MinIO configuration;
- bucket creation;
- uploaded files;
- persistent volumes;
- database containers;
- service ports;
- service URLs;
- backend/frontend container communication;
- local setup documentation.

### Docker data safety rules

No agent may run or recommend destructive Docker commands without explicit warning and approval.

Destructive commands include:

- `docker compose down -v`;
- `docker system prune`;
- `docker system prune -a`;
- `docker volume prune`;
- `docker volume rm`;
- deleting MinIO buckets;
- deleting database volumes;
- deleting uploaded files.

Before any destructive action, the agent must explain:

1. what data may be lost;
2. which containers, volumes, or buckets are affected;
3. whether there is a backup/export option;
4. the safer alternative;
5. whether the action is local-only or could affect shared/prod data.

### MinIO rules

For MinIO-related changes, agents must verify or explicitly mark as unverified:

- bucket names;
- bucket creation/init process;
- access key and secret handling;
- endpoint used by backend;
- endpoint used by frontend/browser;
- CORS;
- bucket policy;
- presigned URL behavior;
- persisted volume;
- local versus production storage differences.

Do not make buckets public unless explicitly required and justified.

Do not commit real MinIO credentials.
## Ubuntu VPS operations

Use `infrastructure-devops-docker` for VPS-related work involving:

- Ubuntu server commands;
- SSH deployment;
- Bash scripts;
- Docker Compose on VPS;
- systemd services;
- firewall configuration;
- server ports;
- server logs;
- Linux permissions;
- deployment directories;
- backups;
- MinIO persistence;
- database persistence;
- production-like restarts.

### VPS safety rules

No agent may recommend destructive VPS commands without explicit warning.

Dangerous commands include:

- `rm -rf`
- `chmod -R 777`
- broad `chown -R`
- `docker compose down -v`
- `docker system prune`
- `docker volume prune`
- deleting MinIO buckets
- deleting database volumes
- overwriting `.env`
- disabling firewall
- exposing internal services publicly

Before any risky VPS action, the agent must explain:

1. what the command does;
2. what data may be lost;
3. whether downtime may occur;
4. whether a backup is needed;
5. how to verify success;
6. how to rollback, if possible.

### VPS deployment rule

For deploys on the VPS, prefer this sequence:

1. Inspect current compose files and scripts.
2. Check current running containers.
3. Check disk space.
4. Check environment variables without exposing secrets.
5. Backup critical data if the operation is risky.
6. Pull/build only what is needed.
7. Restart only affected services where possible.
8. Verify health and logs.
9. Report what was verified and what remains unverified.