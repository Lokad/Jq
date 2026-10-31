# Regex adapter example

Compile [RegexAdapterExample.cs](RegexAdapterExample.cs) into an application
referencing `Lokad.Jq`. Supply an `IJqHost`, your shared `IMemoryBudget` and an
`IJqRegexEngine` adapter:

```csharp
await RegexAdapterExample.RunAsync(host, memoryBudget,
    regexEngine, cancellationToken);
```

Feed UTF-8 JSON `"é item-42"` to standard input. Standard output is:

```json
[true,{"offset":2,"length":7,"id":"42"},{"id":"42"}]
```

The example exercises boolean matching, named captures and Unicode-scalar offsets.
All I/O stays with the host; the application owns the borrowed engine. Jq owns
compiled handles until their subjects, cursors and retained results finish.

Adapters expose actual capture counts and UTF-8 names, prepare the complete subject,
and return cursors with absolute byte ranges. Forward the shared budget and each
call's cancellation token into the provider. `Current` is borrowed until the next
advance; `RetainCurrent` supplies independent result ownership. Follow the
[regex contract](../docs/REGEX.md) for admission, empty-match progression and errors.

The package has no provider dependency or implicit engine. The repository's
temporary PCRE2 0.4 semantic bridge is development tooling, pending qualification
against the producer's prepared-subject and shared-admission APIs. Tests and the
isolated package check compile and execute this application example.
