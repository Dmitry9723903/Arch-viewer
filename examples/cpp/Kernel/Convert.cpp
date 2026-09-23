#include "Units.h"

namespace Example { namespace Kernel {

/// Free functions are how C states most of what it states, and how much of
/// C++ states the part that belongs to no type.
double toKelvin(const Celsius& value)
{
    return value.degrees() + 273.15;
}

// Not a definition: a promise kept elsewhere, and nothing this file holds.
double toFahrenheit(const Celsius& value);

}}
