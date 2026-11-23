# M0.1 implementation handoff

**Deliverable:** an ASP.NET Core solution and React/Vite/TypeScript client that build from a clean checkout, with xUnit tests and CI running the same commands as local development. Use synthetic smoke-test data.

1. Put the solution at the repository root, the ASP.NET Core project in `src/Nexus.Web` and xUnit in `tests/Nexus.IntegrationTests`. Include both .NET projects. The preparation machine has SDK 10.0.401. Record the SDK used in CI.
2. Keep React source in `src/Nexus.Client/src/` and commit the package lockfile. Proxy API calls through Vite in development. Publish the built client and API on the same ASP.NET Core origin.
3. Smoke-test the host over HTTP. Check the published React page, including a direct request to a client route. Select a pipeline for the Git host once the repository is connected.
4. Document root-level restore, build, test, development and publish commands in the README. Verify them in a clean checkout or CI before completing M0.1.

**Next:** M0.2 adds EF Core, PostgreSQL, a separate migration role and integration tests with Testcontainers. Those tests need a container runtime. Demo login, SignalR and a private PDF are acceptance criteria for the full M0 milestone.
