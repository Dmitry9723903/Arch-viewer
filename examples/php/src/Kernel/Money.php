<?php
declare(strict_types=1);

namespace Example\Kernel;

/** An amount of money: a value, not an entity. */
final class Money
{
    public function __construct(
        private readonly float $amount,
        private readonly string $currency,
    ) {
    }

    public function times(float $factor): self
    {
        return new self($this->amount * $factor, $this->currency);
    }
}
