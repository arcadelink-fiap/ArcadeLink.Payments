# ArcadeLink.Payments

Microsserviço de pagamentos (processamento simulado de pagamentos) da arquitetura de microsserviços
orientada a eventos do FIAP Cloud Games (FCG) — Tech Challenge Fase 2.

Decidido como **stateless** (sem banco de dados) — não persiste histórico de transações.

Este serviço **não é exposto pelo API Gateway (Kong)** — o edital só pede Gateway na frente de
Users e Catalog; Payments continua só orientado a eventos, sem tráfego HTTP externo (o único
endpoint HTTP que tem, `GET /health`, é só para health probes internos do k8s).

## Endpoints

| Método | Rota | Descrição |
|---|---|---|
| GET | `/health` | Health check dos probes do k8s; devolve `status`, `service` e `version` (`APP_VERSION`, SHA curto). |

## Como rodar localmente

Pré-requisito: .NET 8 SDK e RabbitMQ rodando (`docker start rabbitmq`) — Payments é stateless, não
precisa de Postgres.

```bash
dotnet restore
dotnet run
# ou, com hot reload:
dotnet watch run
```

Sobe em `http://localhost:5100`. Swagger em `/swagger`. Requisição de `GET /health` pronta em
`ArcadeLink.Payments.Api.http`.

## Segredos para desenvolvimento local (`dotnet user-secrets`)

Ao rodar com `dotnet run`, defina os valores sensíveis fora do repositório (gere senhas com `openssl rand -hex 24`):

```bash
cd src/ArcadeLink.Payments.Api
dotnet user-secrets set "RabbitMq:Username" "<usuario>"
dotnet user-secrets set "RabbitMq:Password" "<senha>"
dotnet user-secrets set "Redis:ConnectionString" "localhost:6379"
```

## Configuração (variáveis de ambiente)

O `appsettings.json` contém apenas configuração **não sensível**; nenhuma senha, chave ou connection string fica no código-fonte. Valores sensíveis são injetados em runtime (compose: `.env`; k8s: Secrets criados por `ArcadeLink.Orchestration/scripts/create-secrets.sh`; dev local: `dotnet user-secrets`). Use a sintaxe de variável de ambiente do ASP.NET Core (`__` no lugar de `:`):

| Variável de ambiente | Descrição | Exemplo |
|---|---|---|
| `RabbitMq__Host` | Host do broker. | `localhost` (dev) / `rabbitmq` (compose/k8s) |
| `RabbitMq__Username` | Usuário do broker (sensível). | `<usuario>` |
| `RabbitMq__Password` | Senha do broker (sensível). | `<senha>` |
| `Payments__ApprovalRate` | Probabilidade (0.0–1.0) de aprovar um pagamento na simulação. Pode ser forçado para `0`/`1` para testes determinísticos. | `0.8` (padrão: 80% `Approved` / 20% `Rejected`) |
| `Redis__ConnectionString` | Connection string do Redis (cache de `Payments:ApprovalRate`; sensível no EKS). | `localhost:6379` (dev) / definido via Secret (k8s) |
| `Messaging__Transport` / `Messaging__AmazonSqsRegion` | `RabbitMq` (padrão) ou `AmazonSqs` (opcional) e a região. | `RabbitMq` / `us-east-2` |
| `APP_VERSION` | Versão exibida em `/health` (build-arg). | SHA curto |

## Docker

```bash
docker build -t arcadelink-payments .
```

A imagem final (`mcr.microsoft.com/dotnet/aspnet:8.0`) roda como usuário não-root e escuta na porta
**8080** (`ASPNETCORE_URLS=http://+:8080`) — não é possível bindar na porta 80 sem root. Em
k8s/compose, mapeie a porta externa (ex.: 80) para o `targetPort: 8080` do container.

Ao subir sozinho (fora do compose, contra a infra local exposta no host):

```bash
docker run -d --network host --name arcadelink-payments arcadelink-payments
```

**Orquestração completa**: o `docker-compose.yml` que sobe a aplicação inteira (Postgres, RabbitMQ e
os 4 serviços) mora no repo âncora, `ArcadeLink.Users` — não há um `docker-compose.yml` neste repo.
Rode a partir de lá com os 4 repositórios clonados lado a lado (mesmo diretório pai); veja o
[README do ArcadeLink.Users](../ArcadeLink.Users/README.md#orquestração-completa-docker-compose)
para instruções completas.

## CI/CD e Kubernetes (EKS)

Em produção este serviço roda no **EKS** (`arcadelink-eks`, namespace `arcadelink`); o guia completo (Terraform,
segredos, ordem de execução, custos, troubleshooting) está no
[README do ArcadeLink.Orchestration](../ArcadeLink.Orchestration/README.md).

**Pipeline** (`.github/workflows/ci-cd.yml`, GitHub Actions; autenticação na AWS por OIDC, sem access keys):

```
pull request  ->  Build e testes (dotnet test)
push na main  ->  Build e testes -> docker build (tag = SHA do commit) -> Trivy (CRITICAL) -> push no ECR `arcadelink/payments`
                  -> kubectl apply (rolling update) -> rollout status (falhou? rollout undo)
```

- **Tag da imagem** = SHA completo do commit; o SHA curto entra como `APP_VERSION` (build-arg) e aparece em
  `GET /health` -> `{"status":"healthy","service":"payments-api","version":"<sha curto>"}`.
- **Sem downtime**: `strategy.rollingUpdate` com `maxUnavailable: 0` e `maxSurge: 1`, `startupProbe`/
  `readinessProbe`/`livenessProbe` em `GET /health` (porta 8080). Se a nova versão não ficar pronta, os pods
  antigos continuam atendendo, o pipeline falha e executa `kubectl rollout undo`.
- O deploy só roda em push na `main`. Variables necessárias no repositório do GitHub: `AWS_REGION`,
  `AWS_ROLE_ARN`, `EKS_CLUSTER_NAME`, `ECR_REGISTRY` (sem Secrets neste repo — os segredos ficam no Orchestration).
- Manifestos em `k8s/` (`deployment.yaml` com `${IMAGE}` substituído por `envsubst`, `service.yaml`,
  `configmap.yaml`, `serviceaccount.yaml`). O ConfigMap contém só configuração **não sensível**; credenciais e
  connection strings vêm do Secret `payments-api-secret` (via `envFrom`), criado pelo `deploy-platform` do Orchestration
  a partir dos GitHub Secrets. O ConfigMap opcional `messaging-config` escolhe o transporte (`Messaging__Transport`:
  `RabbitMq` padrão ou `AmazonSqs`, ver Orchestration).
- Não é exposto pelo Kong: só consome/publica eventos e responde `/health` aos probes internos.
- Postgres, RabbitMQ, Redis, MongoDB, Kong e observabilidade vivem em `ArcadeLink.Orchestration/k8s/`
  (desde a Fase 4, não mais aqui).

## Consumer

- `OrderPlacedEventConsumer` → decide `Approved`/`Rejected` aleatoriamente conforme
  `Payments:ApprovalRate`, loga a decisão e publica `PaymentProcessedEvent`. A partir da Fase 3,
  `Payments:ApprovalRate` não é mais lido direto de `IOptions<PaymentsOptions>` — o consumer depende
  de `IApprovalRateProvider`, implementado por `CachedApprovalRateProvider`
  (`Options/CachedApprovalRateProvider.cs`), que cacheia o valor no Redis com TTL de 30s (chave
  `config:approval-rate`) antes de cair no `IOptions<PaymentsOptions>` como fonte original.

## Testes

```bash
dotnet test
```

`tests/ArcadeLink.Payments.Api.Tests` (xUnit, 4 testes): os 2 originais — `OrderPlacedEventConsumerTests`,
usando `AddMassTransitTestHarness` (bus in-memory) — confirmam que `Payments:ApprovalRate = 1` sempre
publica `Approved` e `= 0` sempre publica `Rejected` — mais `CachedApprovalRateProviderTests`, novo na
Fase 3, cobrindo o cache-aside do Redis (hit retorna o valor cacheado sem tocar `IOptions`, miss lê de
`IOptions` e popula o cache). Não precisa de RabbitMQ/Redis rodando.

## Contratos de eventos

Este serviço **consome** `OrderPlacedEvent` e **publica** `PaymentProcessedEvent`.

Os contratos de eventos (namespace `ArcadeLink.Contracts`) **não são compartilhados via pacote
NuGet** — cada repositório mantém sua própria cópia idêntica (mesmo namespace, mesmo nome, mesmos
campos) em `src/ArcadeLink.Payments.Api/Contracts/`. Isso é intencional e simplifica o desafio; o
MassTransit resolve os exchanges do RabbitMQ pelo nome completo do tipo, então qualquer divergência
entre a cópia deste repo e a dos demais serviços quebra a integração silenciosamente.

Eventos definidos aqui:

- `OrderPlacedEvent(Guid OrderId, Guid UserId, Guid GameId, decimal Price)` — publicado por
  `ArcadeLink.Catalog`.
- `PaymentProcessedEvent(Guid OrderId, Guid UserId, Guid GameId, string Status)` — consumido por
  `ArcadeLink.Catalog` e pela `PurchaseConfirmationFunction` em `ArcadeLink.NotificationsFunction`
  (a partir da Fase 3; era `ArcadeLink.Notifications` na Fase 2) (`Status`: `"Approved"` |
  `"Rejected"`).
