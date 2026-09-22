#pragma once

#include "../Kernel/Units.h"

namespace Example { namespace Readings {

/// One measurement, kept as it was taken. A reading is never corrected in
/// place: a correction is another reading.
struct Reading
{
    Kernel::Celsius value;
    long takenAt;
};

}}
