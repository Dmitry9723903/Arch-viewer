<?php
declare(strict_types=1);

namespace Example\Payroll;

use Example\Kernel\Money;

/** What can be paid. */
interface Payable
{
    public function payFor(float $hours): Money;
}
