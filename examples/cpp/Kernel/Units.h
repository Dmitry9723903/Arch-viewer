#pragma once

namespace Example { namespace Kernel {

/// A temperature in degrees Celsius. A value, not a number: the unit is
/// part of the type so that a pressure cannot be passed where this is meant.
struct Celsius
{
    explicit Celsius(double degrees);

    double degrees() const;

private:
    double _degrees;
};

enum class Scale
{
    Celsius,
    Kelvin,
};

// A brace inside a comment, and the word class, to be ignored: { class Trap
const char* const kLabel = "struct NotADeclaration {";

}}
