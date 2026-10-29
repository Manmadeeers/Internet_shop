# How to run tests via dotnet test

## 1. Run tests from a specific file:

```bash
dotnet test tests/InternetShop.Tests.csproj \
  --filter "FullyQualifiedName~PayOrderTests" \
  --logger "console;verbosity=detailed"
```

## 2. Run all tests

```bash
dotnet test InternetShop.sln --logger "console;verbosity=detailed"
```