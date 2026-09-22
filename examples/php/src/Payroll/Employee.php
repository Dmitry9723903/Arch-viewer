<?php
declare(strict_types=1);

namespace Example\Payroll;

use Example\Kernel\Money;

/**
 * Someone who is paid. Documentation above a declaration belongs with it,
 * and a fragment that starts below it explains nothing.
 */
final class Employee implements Payable
{
    public function __construct(
        private readonly string $name,
        private readonly Money $hourlyRate,
    ) {
    }

    public function payFor(float $hours): Money
    {
        // A brace inside a comment is not a brace: }
        $note = "neither is one inside a string: }";
        return $this->hourlyRate->times($hours);
    }
}
