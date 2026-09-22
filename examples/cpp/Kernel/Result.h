#pragma once

namespace Example { namespace Kernel {

/// Success or a stated failure. Expected failures are values here, not
/// exceptions.
template <typename T>
class Result
{
public:
    static Result<T> ok(const T& value);
    static Result<T> failed(const char* reason);

    bool succeeded() const;

private:
    T _value;
    const char* _reason;
};

}}
