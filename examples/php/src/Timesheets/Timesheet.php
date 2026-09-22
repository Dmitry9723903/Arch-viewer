<?php
declare(strict_types=1);

namespace Example\Timesheets;

use Example\Payroll\Employee;

/**
 * Hours recorded for one period.
 *
 * The deliberate crossing this example exists to show: a timesheet holds an
 * employee from another module instead of its identity. The viewer draws it
 * red; it must not be "fixed".
 */
final class Timesheet
{
    public function __construct(
        private readonly Employee $employee,
        private readonly float $hours,
    ) {
    }

    /** An anonymous class declares nothing to name, and must not appear. */
    public function reviewer(): object
    {
        return new class {
            public function approve(): bool
            {
                return true;
            }
        };
    }
}
