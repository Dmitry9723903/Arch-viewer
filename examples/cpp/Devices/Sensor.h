#pragma once

#include "../Kernel/Units.h"
#include "../Kernel/Result.h"

namespace Example { namespace Devices {

/// A device that reports a temperature. The domain knows what a sensor is
/// and nothing about how one is spoken to.
class Sensor
{
public:
    Sensor(int address);
    virtual ~Sensor();

    virtual Kernel::Result<Kernel::Celsius> read() = 0;

    int address() const;

private:
    int _address;
};

/// A sensor that has stopped answering.
class SilentSensor : public Sensor
{
public:
    SilentSensor(int address);

    Kernel::Result<Kernel::Celsius> read();
};

}}
