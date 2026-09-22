#pragma once

#include "Sensor.h"

// The crossing this example exists for: an adapter of one module reaching
// into the domain of another. The policy forbids it, and the map draws it
// red and names the rule.
#include "../Readings/Reading.h"

namespace Example { namespace Devices {

/// Speaks to a sensor over a serial port.
class SerialSensor : public Sensor
{
public:
    SerialSensor(int address, const char* port);

    Kernel::Result<Kernel::Celsius> read();

    Readings::Reading lastReading() const;

private:
    const char* _port;
    Readings::Reading _last;
};

}}
