# Regex adapter example

Compile [RegexAdapterExample.cs](RegexAdapterExample.cs) into an application
referencing the `Lokad.Jq` package. Supply an `IJqHost` and your shared
`IMemoryBudget`, then call:

```csharp
await RegexAdapterExample.RunAsync(host, memoryBudget,
    JqUtf8RegexEngine.Instance, cancellationToken);
```

Feed UTF-8 JSON `"é item-42"` to standard input. Standard output is:

```json
[true,{"offset":2,"length":7,"id":"42"},{"id":"42"}]
```

This exercises the adapter's boolean and detailed searches, named captures and
Unicode-scalar jq offsets. All I/O stays with the host. The invocation is disposed
after execution, including pending output and descriptor cleanup.

The package already includes the [UTF-8 adapter](../src/Lokad.Jq/JqUtf8RegexEngine.cs).
Pass another `IJqRegexEngine` in the same call to use your provider. Engines are
borrowed; Jq disposes compiled handles after their final borrowers finish.
An adapter forwards the compilation budget and cancellation token to its
provider, retains the complete subject and absolute search offsets, and transfers
capture ownership through `JqRegexMatch`. Syntax errors return `false` from
`TryCompile`; refused admission throws `JqRegexResourceException` with
`IsMemoryAdmission = true`. Cancellation propagates.

The shipped Utf8Regex 0.4.0 provider observes cancellation during compilation and
matching. Its internal storage remains outside the host's shared memory budget.
See [regex behavior](../docs/REGEX.md) for the capture contract and limitations.
Tests and the isolated package check compile and execute this exact example source.
