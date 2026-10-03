# Numeric profile

Lokad.Jq evaluates numbers as IEEE-754 doubles, with integral storage for
integers that fit in a signed 64-bit value. This document records the
profile, its reference divergences, and the evidence behind each decision.
The normative reference is jq 1.8.2 as usually built, with decimal-literal
support enabled (see `configure.ac`: decNumber support is on unless
explicitly disabled).

## Domain and storage

- Arithmetic, comparisons, and conversions operate in the double domain:
  every operand passes through the same value-to-double projection, so
  `1 == 1.0` is true and `0.1 + 0.2 == 0.3` is false.
- Integers that fit in `long` keep integral storage. They render exactly
  through identity and conversion (`9007199254740993 | tostring` is exact),
  but arithmetic still projects them to doubles first.
- As a consequence, decimal-sensitive conditionals comparing exact conversions against double-rounded text take neither upstream branch: `tostring` of a large integral literal stays exact, so the non-decimal comparison against the rounded form is false, while arithmetic and equality on the same literal project to doubles.
- Filter literals follow the same rule: `42` is integral, `1.5` and `1e3`
  are doubles, and `-0` stays a double so it renders with its sign.
- Input decoding follows the same rule, so large integral inputs survive
  identity exactly while still computing as doubles.
- Every numeric consumer (arithmetic, comparison, equality, ordering,
  conversions, path and index resolution, slicing, ranging, implosion)
  funnels through storage-agnostic projections, so `int`-stored values
  (from `explode`, host variables, and internal desugars) behave exactly
  like parsed integers.

## Literals, precision, and overflow

- Decimal fractions round to the nearest double at parse time: `1.10`
  behaves as `1.1`. Exponents are doubles: `1e3` is `1000`.
- Integers past 2^53 lose neighbor distinctions in arithmetic and
  comparison (`9007199254740993 == 9007199254740992` is true), even when
  both sides render exactly.
- Overflow produces infinities (`1e1000`), which render clamped to the
  finite extremes (see below) rather than preserving the literal.

These differ deliberately from decimal-literal builds, which preserve
literal text and compare decimals exactly. This is a permanent scope
decision, not a deferred task: exact-decimal fidelity would require an
exact-decimal arithmetic dependency, while the reference itself enables
decimal literals only as a build configuration, so the double domain above
is the embedding contract. The implementation stays dependency-free (no
native decimal library). Every observable divergence in this section
carries a byte-exact pin: `JqTests.Jq_NumbersFollowDoubleProfile` locks
literal rounding (`1.10` rendering `1.1`, `1e3` rendering `1000`), exact
integral rendering with double-projected arithmetic past 2^53, clamped
overflow rendering with uppercase exponents, and the neither-branch
decimal-sensitive conditionals; the capability queries are pinned false in
`JqTests.Math`, and input-side decoding is pinned through the
large-integral ingress vectors.

## Division and remainder

- Dividing two numbers by a zero divisor fails: `1/0` reports
  `number (1) and number (0) cannot be divided because the divisor is
  zero`. Negative zero counts as zero. This matches the reference
  `binop_divide` behavior, including the operand-shaped message.
- Remainder truncates both operands toward zero with saturation at the
  `long` extremes, propagates NaN, returns `0` for a `-1` divisor, and
  fails on a zero divisor with the `(remainder)` message. This matches
  the reference `binop_mod` semantics; `5.5 % 2` is `1`.

## Non-finite values

- IEEE results can still arise (`sqrt` of a negative, `1 % nan`
  equivalents, overflowing literals). The JSON writer cannot emit them,
  so the renderer follows the reference printer: NaN becomes `null` and
  infinities clamp to the largest finite doubles.
- NaN keeps number type (`sqrt(-1) | type` is `"number"`), never equals
  anything including itself, and sorts as null in the total order.

## Equality and ordering

- Equality is kind-sensitive: `1 == "1"` and `0 == false` are false.
  Numbers compare by double value, strings ordinally, arrays element-wise,
  and objects by key lookup regardless of member order.
- The total order is null, false, true, numbers, strings (Unicode scalar
  order, matching byte order for valid UTF-8), arrays (lexical), and
  objects (sorted keys, then values key by key). NaN orders immediately after null and before every number. This total order is deliberate: the reference comparator maps NaN to null asymmetrically (equal to null one way, ordered the other), so only multi-element sort stability on NaN-versus-null adjacencies can differ while every operator result matches.

## Rendering and culture

- Finite doubles use the shortest round-trip form; integral storage
  renders exactly; negative zero renders as `-0`.
- Exponent notation uses an uppercase `E` (`1E+21`); decimal-literal
  builds use a lowercase `e`. Byte-level exponent case is a known
  formatting divergence.
- All numeric lexing and rendering is culture-invariant; ambient culture
  changes cannot alter JSON or jq numeric syntax (covered by
  `JqTests.Jq_NumericSyntaxIgnoresAmbientCulture`).

## Capability and input notes

- Decimal-capability introspection exists and reports the double domain:
  `have_decnum` and `have_literal_numbers` both evaluate to `false`, so
  decimal-sensitive conditionals take their non-decimal branches.
- Non-finite JSON tokens (`nan`, `inf`, `infinity`, any ASCII case with
  an optional sign) parse like the reference strtod fallback wherever a
  value is due, at top level and nested; anything else (for example `NaN1`)
  still fails.
- `tonumber` accepts the same non-finite spellings (an optional sign with
  case-insensitive `nan`, `inf`, or `infinity`, no surrounding whitespace);
  values render through the double-domain rules above.
