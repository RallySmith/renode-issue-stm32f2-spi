*** Settings ***
Suite Setup                   Setup
Suite Teardown                Teardown
Test Setup                    Reset Emulation
Test Teardown                 Test Teardown
Test Timeout                  10 seconds
Resource                      ${RENODEKEYWORDS}

*** Variables ***
${SCRIPT}                     ${CURDIR}/test.resc
${UART}                       sysbus.usart6


*** Keywords ***
Load Script
    Execute Script            ${SCRIPT}
    Create Terminal Tester    ${UART}
    Create Log Tester         1

*** Test Cases ***
Should Run Test Case
    Load Script
    Start Emulation
    Wait For Line On Uart     INFO:<SPI Access Test>
    Should Not Be In Log      cpu: CPU abort [PC=0x74736554]: Trying to execute code outside RAM or ROM at 0x74736554.
