#include "Sensor.h"

namespace Example { namespace Devices {

Sensor::Sensor(int address) : _address(address) {}

Sensor::~Sensor() {}

int Sensor::address() const { return _address; }

SilentSensor::SilentSensor(int address) : Sensor(address) {}

Kernel::Result<Kernel::Celsius> SilentSensor::read()
{
    // The string below contains braces and a keyword; neither is code.
    const char* reason = "sensor is silent { class Ghost };";
    return Kernel::Result<Kernel::Celsius>::failed(reason);
}

}}
